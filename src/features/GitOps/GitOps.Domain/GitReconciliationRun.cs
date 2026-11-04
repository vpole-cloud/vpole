using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitRunStamp(
    GitSourceIdentity Source,
    string RunId,
    long ConfigurationRevision,
    long Generation,
    long Fence);

public sealed record GitAcceptedSnapshot(
    string Commit,
    string DesiredHash,
    ImmutableArray<AcceptedDeclaration> Resources);

public enum GitRunTrigger { Manual, Webhook, Poll, Dependency, Recovery }

public sealed record GitRunInitiator(
    GitRunTrigger Trigger,
    string? RequestedByUserId);

public sealed record GitRunLease(
    string WorkerId,
    long Attempt,
    DateTime ExpiresAtUtc);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitRunWaiting), typeof(GitRunClaimed), typeof(GitRunFinished))]
public closed record GitRunState;

public sealed record GitRunWaiting : GitRunState;

public sealed record GitRunClaimed(GitRunLease Lease) : GitRunState;

public sealed record GitRunFinished(
    GitRunCompletion Completion,
    DateTime FinishedAtUtc) : GitRunState;

public sealed record GitReconciliationRun(
    [property: BsonId] string Id,
    GitRunStamp Stamp,
    GitAcceptedSnapshot Snapshot,
    GitRunInitiator Initiator,
    GitRunState State)
{
    public GitReconciliationRun Claim(GitRunLease lease, DateTime now)
    {
        GitSourceGuards.Required(lease.WorkerId, nameof(lease.WorkerId));
        GitSourceGuards.Utc(now);
        GitSourceGuards.Utc(lease.ExpiresAtUtc);
        if (lease.Attempt <= 0 || lease.ExpiresAtUtc <= now)
            throw new ArgumentException("A claim requires a positive attempt and a future expiry.", nameof(lease));

        var allowed = State switch
        {
            GitRunWaiting => lease.Attempt == 1,
            GitRunClaimed claimed => claimed.Lease.ExpiresAtUtc <= now && lease.Attempt > claimed.Lease.Attempt,
            GitRunFinished => false
        };
        if (!allowed)
            throw new InvalidOperationException("The run is finished or already held by a live claim.");

        return this with { State = new GitRunClaimed(lease) };
    }

    public GitReconciliationRun Complete(GitRunLease lease, GitRunCompletion completion, DateTime now)
    {
        GitSourceGuards.Utc(now);
        if (State is not GitRunClaimed claimed || claimed.Lease != lease || lease.ExpiresAtUtc <= now)
            throw new InvalidOperationException("Completion requires the current unexpired claim.");

        return this with { State = new GitRunFinished(completion, now) };
    }
}

public static class GitReconciliationRunFactory
{
    public static GitReconciliationRun Create(
        GitRunStamp stamp,
        GitAcceptedSnapshot snapshot,
        GitRunInitiator initiator)
    {
        GitSourceGuards.Identity(stamp.Source);
        GitSourceGuards.Required(stamp.RunId, nameof(stamp.RunId));
        GitSourceGuards.Required(snapshot.Commit, nameof(snapshot.Commit));
        GitSourceGuards.Required(snapshot.DesiredHash, nameof(snapshot.DesiredHash));
        if (stamp.ConfigurationRevision <= 0 || stamp.Generation <= 0 || stamp.Fence <= 0)
            throw new ArgumentException("A run requires positive configuration, generation and fence versions.");
        if (initiator.Trigger == GitRunTrigger.Manual && string.IsNullOrWhiteSpace(initiator.RequestedByUserId))
            throw new ArgumentException("A manual request requires an audit user.", nameof(initiator));
        if (snapshot.Resources.IsDefault || snapshot.Resources.Select(item => (item.Kind, item.Name))
            .Distinct().Count() != snapshot.Resources.Length)
            throw new ArgumentException("A snapshot requires a complete, uniquely addressed resource list.");

        return new(stamp.RunId, stamp, snapshot, initiator, new GitRunWaiting());
    }
}
