using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private GitRepositorySources SourceStore => Services.GetRequiredService<GitRepositorySources>();
    private GitSourceScheduling Scheduler => Services.GetRequiredService<GitSourceScheduling>();

    private IMongoCollection<GitSynchronizationState> Synchronization =>
        Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();

    private Task<GitSynchronizationState> State() => SourceStore.StateAsync(TestData.TenantId, source, default);

    private RepositorySyncSignal Push(string id) => new(TestData.TenantId,
        repository.Provider,
        TestRepositorySnapshots.ConnectionId,
        source,
        "main",
        id);

    [Fact]
    public async Task SchedulingLookupRejectsSourceIdentityFromAnotherTenant()
    {
        await Apply();
        var state = await State();
        await Synchronization.UpdateOneAsync(item => item.Source.SourceId == source,
            Builders<GitSynchronizationState>.Update
                .Set(item => item.Source, new GitSourceIdentity(Id(), source))
                .Set(item => item.RequestedGeneration, state.CompletedGeneration + 1)
                .Set(item => item.NextPollAtUtc, DateTime.UtcNow.AddMinutes(-1)));

        var candidates = await Scheduler.RecoverableSourcesAsync(default);

        Assert.DoesNotContain(candidates, candidate => candidate.SourceId == source);
    }

    [Fact]
    public async Task RepositorySignalsCoalesceAndDuplicateDeliveryDoesNotCreateAnotherGeneration()
    {
        await Apply();
        var signal = Push("delivery-1");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Scheduler.RequestRepositorySyncAsync(signal, default)));
        var state = await State();
        Assert.Equal(2, state.RequestedGeneration);
        await Scheduler.RequestRepositorySyncAsync(Push("delivery-2"), default);
        await Scheduler.RequestRepositorySyncAsync(Push("delivery-3"), default);
        Assert.Equal(4, (await State()).RequestedGeneration);

        await Pipeline.RecoverAsync(new(TestData.TenantId, source));
        state = await State();
        Assert.Equal(4, state.CompletedGeneration);
        Assert.Equal(2,
            await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>()
                .CountDocumentsAsync(item => item.Stamp.Source.SourceId == source));
    }

    [Fact]
    public async Task RepositorySignalsMustMatchEveryIdentityComponent()
    {
        await Apply();
        var signal = Push("unmatched");
        var unmatched = new[]
        {
            signal with { TenantId = Id() }, signal with { ConnectionId = Id() },
            signal with { ExternalRepositoryId = "other" }, signal with { Branch = "feature/main" },
            signal with { Provider = "OtherProvider" }
        };
        foreach (var item in unmatched)
            await Scheduler.RequestRepositorySyncAsync(item, default);
        Assert.Equal(1, (await State()).RequestedGeneration);
    }

    [Fact]
    public async Task DependencyWakeRetainsAcceptedInputAndDoesNotSupersedeNewerSync()
    {
        var initial = await Apply();
        var original = await Pipeline.LatestAsync(source);
        var secret = await Secrets.Find(Builders<Pole.Features.Secrets.Domain.Secret>.Filter.Empty).SingleAsync();
        var signal = new GitDependencyWakeSignal(TestData.TenantId,
            new("Secret", secret.Name),
            secret.Id,
            "values-1");
        await Supply();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Scheduler.WakeDependencyAsync(signal, default)));
        var reserved = await Pipeline.LatestAsync(source);
        Assert.Equal(original.Id, reserved.RetryOfRunId);
        Assert.Equal(original.Commit, reserved.Commit);
        Assert.Equal(GitRunTrigger.Dependency, reserved.Initiator.Trigger);
        Assert.Null(reserved.Initiator.RequestedByUserId);
        Assert.IsType<GitRunWaiting>(reserved.State);
        Assert.Equal(2, (await State()).RequestedGeneration);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.RecoverAsync(new(TestData.TenantId, source))).Status);

        repository.Publish(source, Snapshot("2"), "new-head");
        await SourceStore.RequestSyncAsync(new(TestData.TenantId,
                source,
                1,
                Guid.NewGuid().ToString(),
                new(GitRunTrigger.Manual, TestData.UserId)),
            default);
        var ignored = signal with { SignalId = "values-2" };
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Scheduler.WakeDependencyAsync(ignored, default)));
        Assert.Equal(3, (await State()).RequestedGeneration);
        await Pipeline.RecoverAsync(new(TestData.TenantId, source));
        await Scheduler.WakeDependencyAsync(ignored, default);
        Assert.Equal(3, (await State()).RequestedGeneration);
        Assert.NotEqual(initial.Context!.Authority.Commit, (await Pipeline.LatestAsync(source)).Commit);
    }

    [Fact]
    public async Task PollingRecoversMissedPushAndSameCommitDoesNotRepeatMutations()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var releases = await Releases();
        await Synchronization.UpdateOneAsync(item => item.Source.SourceId == source,
            Builders<GitSynchronizationState>.Update.Set(item => item.NextPollAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Contains(new GitSourceIdentity(TestData.TenantId, source),
            await Scheduler.RecoverableSourcesAsync(default));
        await Pipeline.RecoverAsync(new(TestData.TenantId, source));
        Assert.Equal(GitRunCompletion.AlreadyCurrent,
            Assert.IsType<GitRunFinished>((await Pipeline.LatestAsync(source)).State).Completion);
        Assert.Equal(releases, await Releases());

        repository.Publish(source, Snapshot("2"), "missed-webhook");
        await Synchronization.UpdateOneAsync(item => item.Source.SourceId == source,
            Builders<GitSynchronizationState>.Update.Set(item => item.NextPollAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Contains(new GitSourceIdentity(TestData.TenantId, source),
            await Scheduler.RecoverableSourcesAsync(default));
        await Pipeline.RecoverAsync(new(TestData.TenantId, source));
        Assert.Equal(releases + 1, await Releases());
        Assert.Equal(GitRunTrigger.Poll, (await Pipeline.LatestAsync(source)).Initiator.Trigger);
    }

    [Fact]
    public async Task ScannerRecoversAfterEnqueueFailureWithoutLosingPendingWork()
    {
        await Apply();
        await Scheduler.RequestRepositorySyncAsync(Push("lost-enqueue"), default);
        var failing = new GitSourceRecoveryScanner(Scheduler,
            _ => throw new IOException("test enqueue failure"),
            NullLogger<GitSourceRecoveryScanner>.Instance);
        await failing.ScanAsync(default);
        Assert.Equal(2, (await State()).RequestedGeneration);
        Assert.Equal(1, (await State()).CompletedGeneration);
        var dispatched = new List<GitSourceIdentity>();
        await new GitSourceRecoveryScanner(Scheduler,
            dispatched.Add,
            NullLogger<GitSourceRecoveryScanner>.Instance).ScanAsync(default);
        Assert.Contains(new(TestData.TenantId, source), dispatched);
        await Pipeline.RecoverAsync(new(TestData.TenantId, source));
        Assert.Equal(2, (await State()).CompletedGeneration);
    }
}
