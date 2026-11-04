using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitRunStamp(
    GitSourceIdentity Source,
    string RunId,
    long ConfigurationRevision,
    long Generation,
    long Fence);

public sealed record GitAcceptedOperation(
    string Kind,
    string Name,
    string ResourceId,
    DeclarationPhase Phase);

public sealed record GitAcceptedSnapshot(
    string Commit,
    string DesiredHash,
    string ApiVersion,
    ImmutableArray<AcceptedDeclaration> Resources,
    ImmutableArray<GitAcceptedOperation> Operations);

public enum GitRunTrigger { Manual, Webhook, Poll, Dependency, Recovery }

public sealed record GitRunInitiator(
    GitRunTrigger Trigger,
    string? RequestedByUserId);

public sealed record GitRunLease(
    string WorkerId,
    long Attempt,
    DateTime ExpiresAtUtc);

public sealed record GitClaimIdentity(
    string WorkerId,
    long Attempt);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitInputUnresolved), typeof(GitInputPinned), typeof(GitInputAccepted))]
public closed record GitRunInput;

public sealed record GitInputUnresolved : GitRunInput;

public sealed record GitInputPinned(string Commit) : GitRunInput;

public sealed record GitInputAccepted(GitAcceptedSnapshot Snapshot) : GitRunInput;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitRunWaiting), typeof(GitRunClaimed), typeof(GitRunDelayed), typeof(GitRunFinished))]
public closed record GitRunState;

public sealed record GitRunWaiting(long LastAttempt) : GitRunState;

public sealed record GitRunClaimed(GitRunLease Lease) : GitRunState;

public sealed record GitRunDelayed(
    long LastAttempt,
    DateTime NextAttemptAtUtc) : GitRunState;

public sealed record GitRunFinished(
    GitRunCompletion Completion,
    DateTime FinishedAtUtc) : GitRunState;

public sealed record GitRunDiagnostic(
    string Code,
    string? File,
    int? Document,
    string? Field);

public enum GitRunCompletion { ConfigurationApplied, AlreadyCurrent, Blocked, Failed, Superseded }

public sealed record GitReconciliationRun(
    [property: BsonId] string Id,
    GitRunStamp Stamp,
    GitRepositoryLocation Repository,
    GitRunInput Input,
    GitRunInitiator Initiator,
    string? RetryOfRunId,
    GitRunState State,
    long MutationVersion,
    int ConsecutiveFailures,
    ImmutableArray<GitRunDiagnostic> Diagnostics,
    DateTime CreatedAtUtc)
{
    [BsonIgnore]
    public string? Commit => Input switch
    {
        GitInputUnresolved => null,
        GitInputPinned pinned => pinned.Commit,
        GitInputAccepted accepted => accepted.Snapshot.Commit
    };

    public GitReconciliationRun Pin(string commit)
    {
        if (Input is not GitInputUnresolved)
            throw new InvalidOperationException("A resolved run cannot change its commit.");
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A full immutable commit is required.", nameof(commit));

        return this with { Input = new GitInputPinned(commit.ToLowerInvariant()) };
    }

    public GitReconciliationRun Accept(GitAcceptedSnapshot snapshot)
    {
        if (Input is not GitInputPinned pinned || pinned.Commit != snapshot.Commit ||
            snapshot.Resources.IsDefault || snapshot.Operations.IsDefault ||
            snapshot.Resources.Select(item => (item.Kind, item.Name)).Distinct().Count() != snapshot.Resources.Length)
            throw new InvalidOperationException(
                "Acceptance requires the pinned commit and a complete unique snapshot.");
        foreach (var resource in snapshot.Resources)
        {
            GitSourceGuards.Required(resource.Kind, nameof(resource.Kind));
            GitSourceGuards.Required(resource.Name, nameof(resource.Name));
            GitSourceGuards.Required(resource.ResourceId, nameof(resource.ResourceId));
            GitSourceGuards.Required(resource.Hash, nameof(resource.Hash));
            GitSourceGuards.Required(resource.SpecJson, nameof(resource.SpecJson));
        }

        if (snapshot.Operations.Select(item => (item.Kind, item.Name, item.Phase)).Distinct().Count() !=
            snapshot.Operations.Length ||
            snapshot.Operations.Any(operation => !Enum.IsDefined(operation.Phase) ||
                                                 !snapshot.Resources.Any(resource => resource.Kind == operation.Kind &&
                                                     resource.Name == operation.Name &&
                                                     resource.ResourceId == operation.ResourceId)) ||
            snapshot.Resources.Any(resource => !snapshot.Operations.Any(operation =>
                operation.Kind == resource.Kind && operation.Name == resource.Name)))
            throw new InvalidOperationException("Every operation must retain its accepted resource identity.");

        GitSourceGuards.Required(snapshot.DesiredHash, nameof(snapshot.DesiredHash));
        GitSourceGuards.Required(snapshot.ApiVersion, nameof(snapshot.ApiVersion));

        return this with { Input = new GitInputAccepted(snapshot), ConsecutiveFailures = 0, Diagnostics = [] };
    }

    public GitReconciliationRun Claim(GitRunLease lease, DateTime now)
    {
        GitSourceGuards.Required(lease.WorkerId, nameof(lease.WorkerId));
        GitSourceGuards.Utc(now);
        GitSourceGuards.Utc(lease.ExpiresAtUtc);
        if (lease.ExpiresAtUtc <= now)
            throw new ArgumentException("A lease requires a future expiry.", nameof(lease));

        var previous = State switch
        {
            GitRunWaiting waiting => waiting.LastAttempt,
            GitRunDelayed delayed when delayed.NextAttemptAtUtc <= now => delayed.LastAttempt,
            GitRunClaimed claimed when claimed.Lease.ExpiresAtUtc <= now => claimed.Lease.Attempt,
            _ => throw new InvalidOperationException("The run is not available for a claim.")
        };
        if (lease.Attempt != checked(previous + 1))
            throw new InvalidOperationException("A claim must advance the attempt exactly once.");

        return this with { State = new GitRunClaimed(lease) };
    }

    public GitReconciliationRun Complete(GitClaimIdentity claim, GitRunCompletion completion, DateTime now)
    {
        RequireClaim(claim, now);
        if (completion == GitRunCompletion.ConfigurationApplied && Input is not GitInputAccepted)
            throw new InvalidOperationException("Only an accepted snapshot can be applied.");

        return this with { State = new GitRunFinished(completion, now) };
    }

    public GitReconciliationRun Delay(GitClaimIdentity claim, DateTime now, DateTime next, GitRunDiagnostic diagnostic)
    {
        RequireClaim(claim, now);
        GitSourceGuards.Utc(next);
        if (next <= now)
            throw new ArgumentException("Retry must be scheduled in the future.", nameof(next));

        return this with
        {
            State = new GitRunDelayed(claim.Attempt, next),
            ConsecutiveFailures = checked(ConsecutiveFailures + 1),
            Diagnostics = [diagnostic]
        };
    }

    private void RequireClaim(GitClaimIdentity claim, DateTime now)
    {
        GitSourceGuards.Utc(now);
        if (State is not GitRunClaimed claimed || claimed.Lease.WorkerId != claim.WorkerId ||
            claimed.Lease.Attempt != claim.Attempt || claimed.Lease.ExpiresAtUtc <= now)
            throw new InvalidOperationException("The current unexpired claim is required.");
    }
}

public static class GitReconciliationRunFactory
{
    public static GitReconciliationRun Create(
        GitRunStamp stamp,
        GitRepositoryLocation repository,
        GitRunInitiator initiator,
        DateTime now,
        GitRunInput input,
        string? retryOfRunId)
    {
        GitSourceGuards.Identity(stamp.Source);
        GitSourceGuards.Required(stamp.RunId, nameof(stamp.RunId));
        GitSourceGuards.Utc(now);
        if (stamp.ConfigurationRevision <= 0 || stamp.Generation <= 0 || stamp.Fence <= 0)
            throw new ArgumentException("A run requires positive configuration, generation and fence versions.");
        if (initiator.Trigger == GitRunTrigger.Manual && string.IsNullOrWhiteSpace(initiator.RequestedByUserId))
            throw new ArgumentException("A manual request requires an audit user.", nameof(initiator));

        return new(stamp.RunId,
            stamp,
            repository,
            input,
            initiator,
            retryOfRunId,
            new GitRunWaiting(0),
            0,
            0,
            [],
            now);
    }
}
