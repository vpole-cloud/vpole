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
[BsonKnownTypes(typeof(GitInputUnresolved), typeof(GitInputPinned), typeof(GitInputAccepted), typeof(GitInputLifecycle),
    typeof(GitInputAwaitingApproval))]
public closed record GitRunInput;

public sealed record GitInputUnresolved : GitRunInput;

public sealed record GitInputPinned(string Commit) : GitRunInput;

public sealed record GitInputAccepted(GitAcceptedSnapshot Snapshot) : GitRunInput;

public sealed record GitInputLifecycle(GitLifecycleExecutionPlan Plan) : GitRunInput;

public sealed record GitInputAwaitingApproval(
    GitAcceptedSnapshot Snapshot,
    string PreviewId) : GitRunInput;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitRunWaiting), typeof(GitRunClaimed), typeof(GitRunDelayed), typeof(GitRunWaitingCleanup),
    typeof(GitRunFinished))]
public closed record GitRunState;

public sealed record GitRunWaiting(long LastAttempt) : GitRunState;

public sealed record GitRunClaimed(GitRunLease Lease) : GitRunState;

public sealed record GitRunDelayed(
    long LastAttempt,
    DateTime NextAttemptAtUtc) : GitRunState;

public sealed record GitRunWaitingCleanup(
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

public enum GitRunCompletion
{
    ConfigurationApplied, AlreadyCurrent, LifecycleCompleted, Blocked, Failed, Superseded
}

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
        GitInputAccepted accepted => accepted.Snapshot.Commit,
        GitInputAwaitingApproval approval => approval.Snapshot.Commit,
        GitInputLifecycle lifecycle => lifecycle.Plan.Source.Input switch
        {
            GitPreviewInventoryInput => null,
            GitPreviewRepositoryInput repository => repository.Snapshot.Commit
        }
    };

    [BsonIgnore]
    public GitAcceptedSnapshot? AcceptedSnapshot => Input switch
    {
        GitInputAccepted accepted => accepted.Snapshot,
        GitInputAwaitingApproval approval => approval.Snapshot,
        GitInputLifecycle { Plan.Source.Input: GitPreviewRepositoryInput repository } => repository.Snapshot,
        GitInputUnresolved or GitInputPinned or GitInputLifecycle => null
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

    public GitReconciliationRun AwaitApproval(GitOperationPreview preview)
    {
        if (Input is not GitInputAccepted accepted || preview.State is not GitPreviewAwaitingConfirmation ||
            preview.Source.Source != Stamp.Source ||
            preview.Source.ConfigurationRevision != Stamp.ConfigurationRevision ||
            preview.Source.Input is not GitPreviewRepositoryInput repository ||
            accepted.Snapshot.Commit != repository.Snapshot.Commit ||
            accepted.Snapshot.DesiredHash != repository.Snapshot.DesiredHash)
            throw new InvalidOperationException(
                "Approval requires the complete accepted input and its server preview.");

        return this with { Input = new GitInputAwaitingApproval(repository.Snapshot, preview.Id) };
    }

    public GitReconciliationRun AuthorizePrune(GitOperationPreview preview)
    {
        if (Input is not GitInputAccepted accepted ||
            preview.State is not GitPreviewPolicyAuthorized policy || policy.RunId != Id ||
            policy.ConfigurationRevision != Stamp.ConfigurationRevision || preview.Source.Source != Stamp.Source ||
            preview.Source.Input is not GitPreviewRepositoryInput repository ||
            accepted.Snapshot.Commit != repository.Snapshot.Commit ||
            accepted.Snapshot.DesiredHash != repository.Snapshot.DesiredHash)
            throw new InvalidOperationException("Policy execution must retain this run's exact accepted input.");

        return this with { Input = new GitInputLifecycle(GitLifecycleExecutionPlanFactory.Create(preview)) };
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
            GitRunWaitingCleanup waiting when waiting.NextAttemptAtUtc <= now => waiting.LastAttempt,
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
        if (completion is GitRunCompletion.ConfigurationApplied or GitRunCompletion.AlreadyCurrent &&
            AcceptedSnapshot is null)
            throw new InvalidOperationException("Only an accepted snapshot can be applied.");

        if (completion == GitRunCompletion.LifecycleCompleted && Input is not GitInputLifecycle)
            throw new InvalidOperationException("Only a lifecycle run can complete lifecycle actions.");

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

    public GitReconciliationRun WaitCleanup(GitClaimIdentity claim, DateTime now, DateTime next)
    {
        RequireClaim(claim, now);
        GitSourceGuards.Utc(next);
        if (next <= now)
            throw new ArgumentException("Cleanup recovery must be scheduled in the future.", nameof(next));
        return this with { State = new GitRunWaitingCleanup(claim.Attempt, next) };
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
