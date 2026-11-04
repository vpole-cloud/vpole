namespace Pole.Features.GitOps.Application.Persistence;

public sealed record SnapshotSourceMissing;
public sealed record StaleSnapshotConflict;
public sealed record SnapshotSourceRelocationRequired;
public union CreateSnapshotSourceResult(
    ManualSnapshotSource,
    SnapshotSourceRelocationRequired);

public sealed record SnapshotResourceStatus(
    string Kind,
    string Name,
    string ResourceId,
    string? Blocker,
    bool PendingRemoval,
    bool ConfigurationApplied);
public sealed record SnapshotOperationStatus(
    string Kind,
    string Name,
    string Phase,
    string? Blocker,
    bool Changed);
public sealed record SnapshotSourceStatus(
    string Id,
    string SourceUrl,
    long Fence,
    string? RunId,
    string Status,
    string? ObservedCommit,
    string? AppliedCommit,
    DateTime? NextAttemptAtUtc,
    IReadOnlyList<SnapshotResourceStatus> Resources,
    IReadOnlyList<SnapshotOperationStatus> Operations);
