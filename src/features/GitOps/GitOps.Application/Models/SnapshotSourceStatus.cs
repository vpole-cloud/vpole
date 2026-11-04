using System.Text.Json.Serialization;
using Pole.Features.GitOps.Domain;

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

[JsonConverter(typeof(JsonStringEnumConverter<SnapshotSourceState>))]
public enum SnapshotSourceState
{
    AwaitingSnapshot,
    Applying,
    Blocked,
    ConfigurationApplied,
    UnsupportedStoredSchema,
    StaleExecution
}

public sealed record SnapshotSourceStatus(
    string Id,
    string SourceUrl,
    long Fence,
    string? RunId,
    SnapshotSourceState Status,
    string? ObservedCommit,
    string? AppliedCommit,
    DateTime? NextAttemptAtUtc,
    IReadOnlyList<SnapshotResourceStatus> Resources,
    IReadOnlyList<SnapshotOperationStatus> Operations);

internal static class SnapshotSourceStatusMappings
{
    public static SnapshotSourceState FromDomain(SnapshotProgress progress) => progress switch
    {
        ApplyingSnapshot => SnapshotSourceState.Applying,
        BlockedSnapshot => SnapshotSourceState.Blocked,
        AppliedSnapshot => SnapshotSourceState.ConfigurationApplied,
        UnsupportedSnapshotSchema => SnapshotSourceState.UnsupportedStoredSchema,
        StaleSnapshotExecution => SnapshotSourceState.StaleExecution
    };
}
