using System.Collections.Immutable;

namespace Pole.Features.GitOps.Domain;

public sealed record ExecutionStamp(
    string TenantId,
    string SourceId,
    string RunId,
    long Fence,
    long Attempt);

public sealed record AcceptedResourceBinding(
    string Kind,
    string Name,
    string ResourceId,
    string DesiredHash);

public closed record SnapshotProgress;
public sealed record ApplyingSnapshot : SnapshotProgress;
public sealed record BlockedSnapshot : SnapshotProgress;
public sealed record AppliedSnapshot : SnapshotProgress;
public sealed record UnsupportedSnapshotSchema : SnapshotProgress;
public sealed record StaleSnapshotExecution : SnapshotProgress;

public enum SnapshotAcceptance
{
    Accepted,
    StaleExecution,
    SourceRelocationRequired
}

public enum SnapshotApplyOutcome
{
    Applied,
    Blocked,
    StaleExecution
}

// Configuration progress is deliberately independent of workload runtime readiness.
public sealed record SnapshotExecution(
    ExecutionStamp Stamp,
    string SourceUrl,
    string AttemptedCommit,
    string? AppliedCommit,
    ImmutableArray<AcceptedResourceBinding> Resources,
    SnapshotProgress Progress)
{
    public SnapshotAcceptance EvaluateAcceptance(long expectedFence, string sourceUrl) =>
        Stamp.Fence != expectedFence ? SnapshotAcceptance.StaleExecution :
        SourceUrl != sourceUrl ? SnapshotAcceptance.SourceRelocationRequired : SnapshotAcceptance.Accepted;

    public bool AllowsOperation(ExecutionStamp stamp, AcceptedResourceBinding resource) =>
        Stamp == stamp && Resources.Contains(resource);

    public bool AllowsResourceMutation(ExecutionStamp stamp, DeclarationOwnership ownership) =>
        Stamp == stamp && ownership.CanBeMutatedBy(stamp, ownership.ResourceId) &&
        Resources.Any(resource => resource.Kind == ownership.Kind && resource.Name == ownership.Name &&
            resource.ResourceId == ownership.ResourceId);

    public SnapshotExecution Complete(SnapshotApplyOutcome outcome) => outcome switch
    {
        SnapshotApplyOutcome.Applied => this with { Progress = new AppliedSnapshot(), AppliedCommit = AttemptedCommit },
        SnapshotApplyOutcome.Blocked => this with { Progress = new BlockedSnapshot() },
        SnapshotApplyOutcome.StaleExecution => this with { Progress = new StaleSnapshotExecution() },
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public SnapshotExecution RejectStoredSchema() => this with { Progress = new UnsupportedSnapshotSchema() };
}

public static class SnapshotExecutionFactory
{
    public static SnapshotExecution Create(
        ExecutionStamp stamp,
        string sourceUrl,
        string attemptedCommit,
        string? appliedCommit,
        IEnumerable<AcceptedResourceBinding> resources,
        SnapshotProgress progress)
    {
        if (string.IsNullOrWhiteSpace(stamp.TenantId) || string.IsNullOrWhiteSpace(stamp.SourceId) ||
            string.IsNullOrWhiteSpace(stamp.RunId) || stamp.Fence <= 0 || stamp.Attempt < 0)
            throw new ArgumentException("An execution requires its tenant, source, run and nonnegative fencing identity.", nameof(stamp));
        if (string.IsNullOrWhiteSpace(sourceUrl) || string.IsNullOrWhiteSpace(attemptedCommit))
            throw new ArgumentException("An execution requires its immutable source origin and attempted commit.");
        var bindings = resources.ToImmutableArray();
        if (bindings.Any(resource => string.IsNullOrWhiteSpace(resource.Kind) || string.IsNullOrWhiteSpace(resource.Name) ||
                string.IsNullOrWhiteSpace(resource.ResourceId) || string.IsNullOrWhiteSpace(resource.DesiredHash)) ||
            bindings.Select(resource => (resource.Kind, resource.Name)).Distinct().Count() != bindings.Length)
            throw new ArgumentException("Accepted resources require distinct complete identity/hash bindings.", nameof(resources));
        return new(stamp, sourceUrl, attemptedCommit, appliedCommit, bindings, progress);
    }
}
