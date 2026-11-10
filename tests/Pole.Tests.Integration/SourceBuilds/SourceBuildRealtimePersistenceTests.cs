using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application;
using Pole.Features.Realtime.Contracts;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;
using Pole.Features.SourceBuilds.Application.Realtime;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SourceBuildRealtimePersistenceTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.source-builds/v1/source-build-job-state-changed";

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());

    [Fact]
    public async Task QueueAndClaim_CommitImmutableHints_AndOnlyOneClaimSucceeds()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var service = (await Mediator.Send(SourceBuildTestData.CreateTargetRequest(Data, group.Id))).AssertSuccess();
        var job = Job() with
        {
            WebServiceId = service.WebServiceId,
        };
        var configuration = SourceBuildConfigurationFactory.Create(
            job.TenantId,
            "application-build",
            job.SourceRepositoryId,
            job.WebServiceId,
            job.Recipe,
            new ManualSourceBuildConfigurationOrigin(Data.UserId),
            new SourceBuildConfigurationFactory.CreateOptions(job.ConfigurationId, job.CreatedAtUtc));
        await SourceBuildTestData.SeedRepositoryAsync(DbContext, configuration, Data.UserId);
        Assert.IsType<SourceBuildConfiguration>(
            (await Services.GetRequiredService<CreateSourceBuildConfiguration>()(
                new CreateSourceBuildConfigurationCommand(configuration, 1))).Value);
        var requested = new SourceBuildRequestedEvent(job.Id) { EventCore = EventCore.Create(job.TenantId) };
        Assert.IsType<SourceBuildJob>((await Services.GetRequiredService<QueueSourceBuildJob>()(job, requested)).Value);
        var claim = Services.GetRequiredService<ClaimQueuedSourceBuildJob>();
        var results = await Task.WhenAll(
            claim(new ClaimSourceBuildJobRequest(job.Id, Guid.NewGuid().ToString("N"))),
            claim(new ClaimSourceBuildJobRequest(job.Id, Guid.NewGuid().ToString("N"))));
        Assert.Single(results, result => result is not null);
        Assert.Null(await claim(new ClaimSourceBuildJobRequest(job.Id, "second")));
        var envelopes = await DbContext.OutboxMessages
            .Find(x => x.TenantId == Data.TenantId && x.EventType == EventName)
            .ToListAsync();
        Assert.Equal(2, envelopes.Count);
        Assert.Equal(
            ["Cloning", "Queued"],
            envelopes.Select(envelope => JsonSerializer.Deserialize<SourceBuildJobStateChangedEvent>(
                    envelope.PayloadJson,
                    EventExtensions.SerializerOptions)!.State)
                .Order()
                .ToArray());
        Assert.All(envelopes, envelope => Assert.Equal(job.TenantId, envelope.TenantId));
    }

    [Fact]
    public async Task Claim_OutboxFailureRollsBackClaim()
    {
        var job = Job();
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        await using var provider = FailingOutboxProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ClaimQueuedSourceBuildJob>()(
                new ClaimSourceBuildJobRequest(job.Id, "worker")));
        Assert.IsType<QueuedSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(x => x.Id == job.Id).SingleAsync()).State);
        Assert.Equal(
            0,
            await DbContext.OutboxMessages.CountDocumentsAsync(x =>
                x.TenantId == Data.TenantId && x.EventType == EventName));
    }

    [Theory]
    [InlineData("Succeeded", "Succeeded")]
    [InlineData("Failed", "Failed")]
    public async Task DeploymentCompletion_IsTenantScopedAndDuplicateSafe_AndOmitsFailurePayload(
        string outcome,
        string state)
    {
        var job = DeployingJob();
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        var completion = new SourceBuildDeploymentCompletion(
            job.TenantId,
            "release-pilot",
            outcome,
            "sensitive failure detail",
            job.UpdatedAtUtc.AddSeconds(1));
        var complete = Services.GetRequiredService<CompleteSourceBuildJobOnDeployment>();
        await complete(
            completion with
            {
                TenantId = ObjectId.GenerateNewId().ToString(),
            });
        Assert.IsType<DeployingSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(x => x.Id == job.Id).SingleAsync()).State);
        await complete(completion);
        await complete(completion);
        var envelope = await DbContext.OutboxMessages
            .Find(x => x.TenantId == Data.TenantId && x.EventType == EventName)
            .SingleAsync();
        Assert.Equal(
            state,
            JsonSerializer.Deserialize<SourceBuildJobStateChangedEvent>(
                envelope.PayloadJson,
                EventExtensions.SerializerOptions)!.State);
        Assert.DoesNotContain("sensitive failure detail", envelope.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("image-pilot", envelope.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentCompletion_OutboxFailureRollsBackTerminalState()
    {
        var job = DeployingJob();
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        await using var provider = FailingOutboxProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<CompleteSourceBuildJobOnDeployment>()(
                new SourceBuildDeploymentCompletion(
                    job.TenantId,
                    "release-pilot",
                    "Succeeded",
                    null,
                    job.UpdatedAtUtc.AddSeconds(1))));
        Assert.IsType<DeployingSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(x => x.Id == job.Id).SingleAsync()).State);
        Assert.Equal(
            0,
            await DbContext.OutboxMessages.CountDocumentsAsync(x =>
                x.TenantId == Data.TenantId && x.EventType == EventName));
    }

    [Fact]
    public async Task ProgressCas_CommitsStateAndSanitizedOutbox_AndRejectsStaleReplacement()
    {
        var current = Job().StartCloning(DateTime.UtcNow.AddMinutes(-1)) with
        {
            Execution = new ClaimedSourceBuildExecution("worker", 1, DateTime.UtcNow.AddMinutes(2)),
        };
        await DbContext.SourceBuildJobs.InsertOneAsync(current);
        var next = current.StartAnalyzing(current.UpdatedAtUtc.AddSeconds(1));
        using (TraceContext.Push(TraceContext.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "correlation-pilot")))
        {
            Assert.True(await Services.GetRequiredService<TryPersistSourceBuildJob>()(current, next));
        }

        Assert.False(
            await Services.GetRequiredService<TryPersistSourceBuildJob>()(
                current,
                current.Fail(
                    SourceBuildPhase.Cloning,
                    "test",
                    "sensitive failure detail",
                    next.UpdatedAtUtc.AddSeconds(1))));
        var foreign = current with
        {
            TenantId = ObjectId.GenerateNewId().ToString(),
        };
        Assert.False(
            await Services.GetRequiredService<TryPersistSourceBuildJob>()(
                foreign,
                foreign.StartAnalyzing(next.UpdatedAtUtc.AddSeconds(1))));

        Assert.IsType<AnalyzingSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(x => x.Id == current.Id).SingleAsync()).State);
        var envelope = await DbContext.OutboxMessages
            .Find(x => x.TenantId == Data.TenantId && x.EventType == EventName)
            .SingleAsync();
        Assert.Equal(current.TenantId, envelope.TenantId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", envelope.TraceId);
        Assert.Equal("correlation-pilot", envelope.CorrelationId);
        var message = JsonSerializer.Deserialize<SourceBuildJobStateChangedEvent>(
            envelope.PayloadJson,
            EventExtensions.SerializerOptions)!;
        Assert.Equal(next.Id, message.BuildJobId);
        Assert.Equal(next.WebServiceId, message.WebServiceId);
        Assert.Equal("Analyzing", message.State);
        Assert.Equal(next.UpdatedAtUtc, message.UpdatedAtUtc);
        Assert.DoesNotContain("FROM scratch", envelope.PayloadJson, StringComparison.Ordinal);
        var notices = new List<RealtimeChange>();
        var consumer = new NotifySourceBuildStateChangedConsumer((change, _) =>
        {
            notices.Add(change);
            return Task.CompletedTask;
        });
        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);
        Assert.All(
            notices,
            notice => Assert.Equal(new RealtimeChange("source-builds", next.TenantId, next.WebServiceId), notice));
    }

    [Fact]
    public async Task ProgressCas_OutboxFailureRollsBackStateAndOutbox()
    {
        var current = Job().StartCloning(DateTime.UtcNow.AddMinutes(-1)) with
        {
            Execution = new ClaimedSourceBuildExecution("worker", 1, DateTime.UtcNow.AddMinutes(2)),
        };
        await DbContext.SourceBuildJobs.InsertOneAsync(current);
        await using var provider = FailingOutboxProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<TryPersistSourceBuildJob>()(
                current,
                current.StartAnalyzing(current.UpdatedAtUtc.AddSeconds(1))));

        Assert.IsType<CloningSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(x => x.Id == current.Id).SingleAsync()).State);
        Assert.Equal(
            0,
            await DbContext.OutboxMessages.CountDocumentsAsync(x =>
                x.TenantId == Data.TenantId && x.EventType == EventName));
    }

    private ServiceProvider FailingOutboxProvider()
    {
        var registrations = new ServiceCollection().AddLogging()
            .AddSingleton(Fixture.Client)
            .AddSingleton(Fixture.Database)
            .AddSingleton<MongoRunTransaction>();
        registrations.AddSingleton<StoreOutboxWrites>(async (
            session, writes,
            ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected outbox failure.");
        });
        registrations.AddSourceBuildsFeature(new ConfigurationBuilder().Build());
        return registrations.BuildServiceProvider();
    }

    private SourceBuildJob Job() => SourceBuildJobFactory.CreateManual(
        Data.TenantId,
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        "main",
        "abc123",
        new SourceBuildInputVersion(1, 1, 1),
        new SourceBuildRecipe("FROM scratch"),
        Data.UserId,
        null,
        new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow.AddMinutes(-2)));

    private SourceBuildJob DeployingJob()
    {
        var job = Job();
        var now = job.UpdatedAtUtc;
        return job.StartCloning(now.AddSeconds(1))
            .StartAnalyzing(now.AddSeconds(2))
            .StartBuilding(now.AddSeconds(3))
            .StartPushing(SourceBuildArtifactFactory.Create("image-pilot", "digest-pilot"), now.AddSeconds(4))
            .StartDeploying(
                SourceBuildArtifactFactory.Create("image-pilot", "digest-pilot"),
                "release-pilot",
                now.AddSeconds(5));
    }
}
