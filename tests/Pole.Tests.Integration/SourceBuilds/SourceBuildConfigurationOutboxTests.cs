using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildConfigurationOutboxTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.source-builds/v1/source-build-configuration-changed";

    [Fact]
    public async Task CrudEmitsOnlyAcceptedChangesAndPreservesRevisionCas()
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var configuration = Configuration();
        var create = scope.ServiceProvider.GetRequiredService<CreateSourceBuildConfiguration>();
        Assert.IsType<SourceBuildConfiguration>((await create(new(configuration, 1))).Value);
        Assert.IsType<SourceBuildConfigurationNameConflict>((await create(new(
            configuration with { Id = ObjectId.GenerateNewId().ToString() }, 1))).Value);
        var stored = await DbContext.SourceBuildConfigurations.Find(row => row.Id == configuration.Id).SingleAsync();
        var update = scope.ServiceProvider.GetRequiredService<UpdateSourceBuildConfiguration>();
        Assert.IsType<SourceBuildConfiguration>((await update(new(stored, stored.Recipe, stored.UpdatedAtUtc))).Value);
        var next = Assert.IsType<SourceBuildConfiguration>((await update(new(stored,
            stored.Recipe with
            {
                Dockerfile = "private-recipe-content"
            },
            stored.UpdatedAtUtc.AddSeconds(1)))).Value);
        Assert.IsType<SourceBuildConfigurationUpdateConflict>(
            (await update(new(stored, stored.Recipe, next.UpdatedAtUtc.AddSeconds(1)))).Value);
        var delete = scope.ServiceProvider.GetRequiredService<DeleteSourceBuildConfiguration>();
        Assert.IsType<SourceBuildConfigurationRemovalRecorded>((await delete(configuration.TenantId, configuration.Id))
            .Value);
        Assert.IsType<SourceBuildConfigurationNotFound>((await delete(configuration.TenantId, configuration.Id)).Value);
        var rows = await DbContext.OutboxMessages.Find(row => row.EventType == EventName).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows,
            row =>
            {
                var message =
                    JsonSerializer.Deserialize<SourceBuildConfigurationChangedEvent>(row.PayloadJson,
                        EventExtensions.SerializerOptions)!;
                Assert.Equal(configuration.WebServiceId, message.WebServiceId);
                Assert.Equal(configuration.TenantId, message.EventCore.TenantId);
                Assert.DoesNotContain("private-recipe-content", row.PayloadJson);
            });
    }

    [Fact]
    public async Task CleanupIsTenantScopedRetainsHistoryAndRepeatedEmptyCleanupIsQuiet()
    {
        var configuration = Configuration();
        var foreign = configuration with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TenantId = ObjectId.GenerateNewId().ToString()
        };
        await DbContext.SourceBuildConfigurations.InsertManyAsync([configuration, foreign]);
        var job = SourceBuildJobFactory.CreateManual(
            configuration.TenantId,
            configuration.Id,
            configuration.SourceRepositoryId,
            configuration.WebServiceId,
            "main",
            "commit",
            new SourceBuildInputVersion(1, 1, 1),
            configuration.Recipe,
            TestData.UserId,
            null,
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        var cleanup = Services.GetRequiredService<CleanupSourceBuildConfigurations>();
        await cleanup(configuration.TenantId, configuration.WebServiceId);
        await cleanup(configuration.TenantId, configuration.WebServiceId);
        Assert.Equal(foreign.Id,
            (await DbContext.SourceBuildConfigurations.Find(FilterDefinition<SourceBuildConfiguration>.Empty)
                .SingleAsync()).Id);
        Assert.Equal(1, await DbContext.SourceBuildJobs.CountDocumentsAsync(row => row.Id == job.Id));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("cleanup")]
    public async Task OutboxFailureRollsBackAcceptedConfigurationMutation(string mutation)
    {
        var configuration = Configuration();
        if (mutation != "create")
            await DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        var before = await DbContext.SourceBuildConfigurations.Find(row => row.Id == configuration.Id)
            .SingleOrDefaultAsync();
        await using var provider = Provider(fail: true);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation switch
        {
            "update" => scope.ServiceProvider.GetRequiredService<UpdateSourceBuildConfiguration>()(new(before!,
                configuration.Recipe with
                {
                    Dockerfile = "changed"
                },
                configuration.UpdatedAtUtc.AddSeconds(1))),
            "delete" => scope.ServiceProvider.GetRequiredService<DeleteSourceBuildConfiguration>()(
                configuration.TenantId,
                configuration.Id),
            "cleanup" => scope.ServiceProvider.GetRequiredService<CleanupSourceBuildConfigurations>()(
                configuration.TenantId,
                configuration.WebServiceId),
            _ => scope.ServiceProvider.GetRequiredService<CreateSourceBuildConfiguration>()(new(configuration, 1))
        });
        var after = await DbContext.SourceBuildConfigurations.Find(row => row.Id == configuration.Id)
            .SingleOrDefaultAsync();
        Assert.Equal(before?.UpdatedAtUtc, after?.UpdatedAtUtc);
        Assert.Equal(before?.Recipe.Dockerfile, after?.Recipe.Dockerfile);
        Assert.Equal(before?.Id, after?.Id);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    private ServiceProvider Provider(bool fail = false)
    {
        var services = new ServiceCollection().AddSingleton(Fixture.Database).AddSingleton(Fixture.Client)
            .AddSingleton<MongoRunTransaction>();
        services.AddSingleton(Services.GetRequiredService<IGitOpsMutations>());
        services.AddSingleton<FenceSourceRepository>((_, session, _) =>
        {
            Assert.True(session.IsInTransaction);
            return Task.FromResult<FenceSourceRepositoryOutcome>(new SourceRepositoryFenced());
        });
        services.AddSingleton<LoadSourceBuildTarget>((request, session, _) =>
        {
            Assert.True(session.IsInTransaction);
            return Task.FromResult<LoadSourceBuildTargetOutcome>(
                new SourceBuildWebServiceNotFound(request.WebServiceId));
        });
        services.AddSourceBuildsFeature(new ConfigurationBuilder().Build());
        services.AddSingleton<AdmitSourceBuildWebService>((request, session, _) =>
        {
            Assert.True(session.IsInTransaction);
            return Task.FromResult<AdmitSourceBuildWebServiceOutcome>(new SourceBuildWebServiceAdmitted());
        });
        services.AddSingleton<StoreOutboxWrites>(async (session, writes, ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            if (fail)
                throw new InvalidOperationException("Injected outbox failure.");
        });
        return services.BuildServiceProvider();
    }

    private static SourceBuildConfiguration Configuration() => SourceBuildConfigurationFactory.Create(
        TestData.TenantId,
        "application-build",
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        new("FROM scratch"),
        new ManualSourceBuildConfigurationOrigin(TestData.UserId),
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
}
