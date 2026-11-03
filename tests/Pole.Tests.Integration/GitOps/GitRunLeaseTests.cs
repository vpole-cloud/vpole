using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class GitRunLeaseTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private GitRunStore Store => Services.GetRequiredService<GitRunStore>();
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();

    private IMongoCollection<GitReconciliationRun> Runs =>
        Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>();

    [Fact]
    public async Task LiveClaimRequiresStoredIdentityFenceAndAnUnexpiredMongoLease()
    {
        var source = await CreateSource();
        var claimed = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id), "filter-worker", default)).Value);
        var stamp = claimed.Run.Stamp;
        var identity = claimed.Handle.Identity;
        Assert.Equal(1, await Runs.CountDocumentsAsync(GitRepositoryFilters.LiveClaim(stamp, identity)));
        Assert.Equal(0, await Runs.CountDocumentsAsync(GitSchedulingFilters.Recoverable()));

        var changed = new[]
        {
            stamp with { Source = stamp.Source with { TenantId = MongoDB.Bson.ObjectId.GenerateNewId().ToString() } },
            stamp with { Source = stamp.Source with { SourceId = MongoDB.Bson.ObjectId.GenerateNewId().ToString() } },
            stamp with { RunId = MongoDB.Bson.ObjectId.GenerateNewId().ToString() },
            stamp with { ConfigurationRevision = stamp.ConfigurationRevision + 1 },
            stamp with { Fence = stamp.Fence + 1 }
        };
        foreach (var candidate in changed)
            Assert.Equal(0, await Runs.CountDocumentsAsync(GitRepositoryFilters.LiveClaim(candidate, identity)));
        Assert.Equal(0, await Runs.CountDocumentsAsync(GitRepositoryFilters.LiveClaim(stamp,
            identity with { WorkerId = "other-worker" })));
        Assert.Equal(0, await Runs.CountDocumentsAsync(GitRepositoryFilters.LiveClaim(stamp,
            identity with { Attempt = identity.Attempt + 1 })));

        await Expire(claimed);
        Assert.Equal(0, await Runs.CountDocumentsAsync(GitRepositoryFilters.LiveClaim(stamp, identity)));
        Assert.Equal(1, await Runs.CountDocumentsAsync(GitSchedulingFilters.Recoverable()));
    }

    [Fact]
    public async Task ConcurrentWorkersHaveOneLeaseAndHeartbeatKeepsIdentity()
    {
        var source = await CreateSource();
        var claims = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(index => Store.ClaimAsync(
                new(source.TenantId, source.Id),
                $"worker-{index}",
                default)));
        var claim = Assert.Single(claims.Select(item => item.Value).OfType<GitWorkClaim>());
        Assert.Equal(1, claim.Handle.Identity.Attempt);
        Assert.True(await Store.HeartbeatAsync(claim.Handle, default));
        var persisted = await Sources.RunAsync(source.TenantId, source.Id, claim.Run.Id, default);
        var lease = Assert.IsType<GitRunClaimed>(persisted!.State).Lease;
        Assert.Equal(claim.Handle.Identity, new(lease.WorkerId, lease.Attempt));
        Assert.True(lease.ExpiresAtUtc >= ((GitRunClaimed)claim.Run.State).Lease.ExpiresAtUtc);
        Assert.IsType<GitNoWorkAvailable>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "competitor",
            default)).Value);
    }

    [Fact]
    public async Task ExpiredClaimIsReclaimedAndCannotPinOrComplete()
    {
        var source = await CreateSource();
        var first = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "first",
            default)).Value);
        await Expire(first);
        var second = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "second",
            default)).Value);
        Assert.Equal(first.Run.Id, second.Run.Id);
        Assert.Equal(2, second.Handle.Identity.Attempt);
        Assert.False(await Store.PinAsync(first.Handle, Commit, default));
        Assert.False(await Store.CompleteAsync(first.Handle, GitRunCompletion.Failed, [], default));
        Assert.True(await Store.PinAsync(second.Handle, Commit, default));
        Assert.True(await Store.CompleteAsync(second.Handle,
            GitRunCompletion.Failed,
            [new("ReadUnavailable", null, null, null)],
            default));
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(1, state.CompletedGeneration);
        Assert.Equal(Commit, state.Commits.Observed);
        Assert.Null(state.Commits.Attempted);
        Assert.Null(state.Commits.FullyApplied);
    }

    [Fact]
    public async Task ConfigurationUpdateFencesTheClaimAndCreatesANewRevisionRun()
    {
        var source = await CreateSource();
        var first = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "first",
            default)).Value);
        await Sources.ChangePolicyAsync(new(source.TenantId, source.Id, 1, new(TimeSpan.FromMinutes(10))), default);
        Assert.False(await Store.HeartbeatAsync(first.Handle, default));
        Assert.False(await Store.PinAsync(first.Handle, Commit, default));
        var old = await Sources.RunAsync(source.TenantId, source.Id, first.Run.Id, default);
        Assert.Equal(GitRunCompletion.Superseded, Assert.IsType<GitRunFinished>(old!.State).Completion);
        var next = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "next",
            default)).Value);
        Assert.Equal(2, next.Run.Stamp.ConfigurationRevision);
        Assert.True(next.Run.Stamp.Fence > first.Run.Stamp.Fence);
    }

    [Fact]
    public async Task NewSignalsCoalesceWithoutChangingAcceptedInputOrLosingGeneration()
    {
        var source = await CreateSource();
        var claim = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "worker",
            default)).Value);
        Assert.True(await Store.PinAsync(claim.Handle, Commit, default));
        Assert.True(await Store.AcceptAsync(claim.Handle,
            new(Commit, "desired", SnapshotSchema.ApiVersion, [], []),
            default));
        for (var index = 0; index < 3; index++)
            await Sources.RequestSyncAsync(new(source.TenantId,
                    source.Id,
                    1,
                    Guid.NewGuid().ToString("D"),
                    new(GitRunTrigger.Manual, TestData.UserId)),
                default);
        Assert.True(await Store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(4, state.RequestedGeneration);
        Assert.Equal(1, state.CompletedGeneration);
        Assert.Equal(new(Commit, Commit, Commit), state.Commits);
        var next = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "next",
            default)).Value);
        Assert.Equal(4, next.Run.Stamp.Generation);
        Assert.Equal(Commit, next.PreviouslyAppliedCommit);
        Assert.IsType<GitInputUnresolved>(next.Run.Input);
        Assert.True(await Store.PinAsync(next.Handle, Commit, default));
        await Expire(next);
        var resumed = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "recovered",
            default)).Value);
        Assert.Equal(Commit, resumed.PreviouslyAppliedCommit);
        Assert.True(await Store.AcceptAsync(resumed.Handle, resumed.PreviouslyAppliedSnapshot!, default));
        Assert.True(await Store.CompleteAsync(resumed.Handle, GitRunCompletion.AlreadyCurrent, [], default));
    }

    [Fact]
    public async Task BackoffPreservesCommitAndStopsAfterFiveFailures()
    {
        var source = await CreateSource();
        var claim = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "worker",
            default)).Value);
        Assert.True(await Store.PinAsync(claim.Handle, Commit, default));
        for (var failure = 1; failure <= 5; failure++)
        {
            Assert.True(await Store.DelayAsync(claim.Handle, new("TemporaryUnavailable", null, null, null), default));
            var persisted = (await Sources.RunAsync(source.TenantId, source.Id, claim.Run.Id, default))!;
            Assert.Equal(Commit, persisted.Commit);
            Assert.Equal(failure, persisted.ConsecutiveFailures);
            if (failure == 5)
            {
                Assert.Equal(GitRunCompletion.Failed, Assert.IsType<GitRunFinished>(persisted.State).Completion);
                break;
            }

            Assert.IsType<GitNoWorkAvailable>((await Store.ClaimAsync(
                new(source.TenantId, source.Id),
                "early",
                default)).Value);
            var delayed = Assert.IsType<GitRunDelayed>(persisted.State);
            await Runs.UpdateOneAsync(item => item.Id == persisted.Id,
                Builders<GitReconciliationRun>.Update.Set(item => item.State,
                    delayed with { NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1) }));
            claim = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
                new(source.TenantId, source.Id),
                "restarted",
                default)).Value);
            Assert.Equal(failure + 1, claim.Handle.Identity.Attempt);
        }

        Assert.Equal(1, (await Sources.StateAsync(source.TenantId, source.Id, default)).CompletedGeneration);
    }

    [Fact]
    public async Task RetryReservesItsInputBeforeANewerSyncCanBeClaimed()
    {
        var source = await CreateSource();
        var first = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "first",
            default)).Value);
        Assert.True(await Store.PinAsync(first.Handle, Commit, default));
        Assert.True(await Store.AcceptAsync(first.Handle,
            new(Commit, "desired", SnapshotSchema.ApiVersion, [], []),
            default));
        Assert.True(await Store.CompleteAsync(first.Handle, GitRunCompletion.Blocked, [], default));
        var request = new GitSourceCommand(source.TenantId,
            source.Id,
            1,
            Guid.NewGuid().ToString("D"),
            new(GitRunTrigger.Manual, TestData.UserId));
        Assert.IsType<GitSourceScheduled>((await Sources.RetryAsync(new(request, first.Run.Id), default)).Value);
        await Sources.RequestSyncAsync(request with { RequestKey = Guid.NewGuid().ToString("D") }, default);
        var retry = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "retry",
            default)).Value);
        Assert.Equal(first.Run.Id, retry.Run.RetryOfRunId);
        Assert.Equal(Commit, retry.Run.Commit);
        Assert.IsType<GitInputAccepted>(retry.Run.Input);
        Assert.Equal(2, retry.Run.Stamp.Generation);
        Assert.Equal(3, (await Sources.StateAsync(source.TenantId, source.Id, default)).RequestedGeneration);
    }

    [Fact]
    public async Task FullyAppliedRequiresMatchingSuccessfulReceipts()
    {
        var source = await CreateSource();
        var claim = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "worker",
            default)).Value);
        Assert.True(await Store.PinAsync(claim.Handle, Commit, default));
        var resourceId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var snapshot = new GitAcceptedSnapshot(Commit,
            "desired",
            SnapshotSchema.ApiVersion,
            [new("ResourceGroup", "group", "hash", "{}", resourceId)],
            [new("ResourceGroup", "group", resourceId, DeclarationPhase.Prepare)]);
        Assert.True(await Store.AcceptAsync(claim.Handle, snapshot, default));
        Assert.False(await Store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
        Assert.Null((await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.FullyApplied);
        var receipts = Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>();
        var receipt = new GitOperationReceipt("receipt",
            source.TenantId,
            source.Id,
            claim.Run.Id,
            claim.Run.Stamp.Fence,
            "ResourceGroup",
            "group",
            "Prepare",
            resourceId,
            "wrong-hash",
            null,
            false,
            1, null);
        await receipts.InsertOneAsync(receipt);
        Assert.False(await Store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
        await receipts.ReplaceOneAsync(item => item.Id == receipt.Id, receipt with { DesiredHash = "hash" });
        Assert.False(await Store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
        var plan = GitPlannedOperationFactory.Create(
            new(claim.Run.Stamp, new("ResourceGroup", "group", resourceId), DeclarationPhase.Prepare),
            new(Pole.Features.GitOps.Primitives.GitPlannedAction.Create, null, null, [], []), DateTime.UtcNow);
        await Services.GetRequiredService<IMongoCollection<GitPlannedOperation>>().InsertOneAsync(plan);
        Assert.True(await Store.CompleteAsync(claim.Handle, GitRunCompletion.ConfigurationApplied, [], default));
        Assert.Equal(Commit, (await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.FullyApplied);
    }

    [Fact]
    public async Task PinStoresTheSameCanonicalCommitInRunAndObservedProgress()
    {
        var source = await CreateSource();
        var claim = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(
            new(source.TenantId, source.Id),
            "worker",
            default)).Value);
        Assert.True(await Store.PinAsync(claim.Handle, Commit.ToUpperInvariant(), default));
        var run = await Sources.RunAsync(source.TenantId, source.Id, claim.Run.Id, default);
        Assert.Equal(Commit, run!.Commit);
        Assert.Equal(Commit, (await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.Observed);
    }

    private Task Expire(GitWorkClaim claim) => Runs.UpdateOneAsync(item => item.Id == claim.Run.Id,
        Builders<GitReconciliationRun>.Update.Set(item => item.State,
            new GitRunClaimed(new(claim.Handle.Identity.WorkerId,
                claim.Handle.Identity.Attempt,
                DateTime.UtcNow.AddMinutes(-1)))));

    private async Task<RepositorySource> CreateSource() => Assert.IsType<RepositorySource>((await Sources.CreateAsync(
        new(TestData.TenantId,
            Guid.NewGuid().ToString("D"),
            new("Example", "connection", "repository", "main", ""),
            new(TimeSpan.FromMinutes(5)),
            new(TestData.UserId, DateTime.UtcNow),
            "https://example.test/repository"),
        default)).Value);
}
