using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Migrations;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildConfigurationIdentityTests(Pole.Testing.MongoReplicaSetFixture fixture)
    : IClassFixture<Pole.Testing.MongoReplicaSetFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateConfiguration_ReturnsTypedConflictBeforeAdmittingAnotherImageBinding(bool sameName)
    {
        var current = SourceBuildConfigurationFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "application-build",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin("author"),
            SourceBuildConfigurationFactory.DefaultOptions);
        var configurations = fixture.Database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations");
        await configurations.InsertOneAsync(current);
        var incoming = current with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TechnicalName = sameName ? current.TechnicalName : "another-build",
            WebServiceId = sameName ? ObjectId.GenerateNewId().ToString() : current.WebServiceId
        };
        var admissions = 0;
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Client)
            .AddSingleton(fixture.Database).AddSingleton<MongoRunTransaction>().AddSingleton(fixture.StoreOutboxWrites)
            .AddSingleton<FenceSourceRepository>((_, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<FenceSourceRepositoryOutcome>(new SourceRepositoryFenced());
            })
            .AddSingleton<AdmitSourceBuildWebService>((request, _, _) =>
            {
                admissions++;
                return Task.FromResult<AdmitSourceBuildWebServiceOutcome>(
                    new SourceBuildWebServiceNotFound(request.WebServiceId));
            })
            .AddSourceBuildsFeature(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var result = await services.GetRequiredService<CreateSourceBuildConfiguration>()(new(incoming, 1));
        if (sameName)
            Assert.IsType<SourceBuildConfigurationNameConflict>(result.Value);
        else
            Assert.IsType<SourceBuildConfigurationAlreadyExists>(result.Value);

        Assert.Equal(0, admissions);
        Assert.Equal(1, await configurations.CountDocumentsAsync(item => item.TenantId == current.TenantId));
        Assert.Equal(0, await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(new BsonDocument("TenantId", current.TenantId)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentConfigurationCreation_PreservesNamedUniquenessAndOneOutboxWrite(bool sameName)
    {
        await new SourceBuildMigration().Up(fixture.Database);
        var first = SourceBuildConfigurationFactory.Create(
            ObjectId.GenerateNewId().ToString(), "application-build", ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), new("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin("author"), SourceBuildConfigurationFactory.DefaultOptions);
        var second = first with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TechnicalName = sameName ? first.TechnicalName : "another-build",
            WebServiceId = sameName ? ObjectId.GenerateNewId().ToString() : first.WebServiceId
        };
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Client)
            .AddSingleton(fixture.Database).AddSingleton<MongoRunTransaction>().AddSingleton(fixture.StoreOutboxWrites)
            .AddSingleton<FenceSourceRepository>((_, _, _) =>
                Task.FromResult<FenceSourceRepositoryOutcome>(new SourceRepositoryFenced()))
            .AddSingleton<AdmitSourceBuildWebService>(async (_, session, ct) =>
            {
                Assert.True(session.IsInTransaction);
                if (Interlocked.Increment(ref arrivals) >= 2)
                    admitted.TrySetResult();

                await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return new SourceBuildWebServiceAdmitted();
            })
            .AddSourceBuildsFeature(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var create = services.GetRequiredService<CreateSourceBuildConfiguration>();
        var results = await Task.WhenAll(create(new(first, 1)), create(new(second, 1)));
        Assert.Single(results, item => item is SourceBuildConfiguration);
        if (sameName)
            Assert.Single(results, item => item is SourceBuildConfigurationNameConflict);
        else
            Assert.Single(results, item => item is SourceBuildConfigurationAlreadyExists);

        Assert.Equal(1, await fixture.Database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations")
            .CountDocumentsAsync(item => item.TenantId == first.TenantId));
        Assert.Equal(1, await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(new BsonDocument("TenantId", first.TenantId)));
    }

}
