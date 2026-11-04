using System.Text.Json.Serialization;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryRunStatus>))]
public enum RepositoryRunStatus
{
    Queued, Resolving, Fetching, Applying, WaitingRetry, ConfigurationApplied,
    AlreadyCurrent, Blocked, Failed, Superseded
}

public sealed record RepositoryLocationView(
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory,
    string DisplayUrl);

public sealed record RepositorySynchronizationView(
    long RequestedGeneration,
    long CompletedGeneration,
    string? ObservedCommit,
    string? AttemptedCommit,
    string? FullyAppliedCommit,
    string? CurrentRunId,
    string? LatestRunId);

public sealed record RepositoryInventorySummary(
    long Owned,
    long PendingRemoval);

public sealed record RepositorySourceView(
    string Id,
    long Revision,
    RepositoryLocationView Repository,
    int PollIntervalSeconds,
    RepositoryRunStatus Status,
    RepositorySynchronizationView Synchronization,
    RepositoryInventorySummary Inventory,
    RepositoryRunView? LatestRun);

public sealed record RepositoryRunDiagnosticView(
    string Code,
    string? File,
    int? Document,
    string? Field);

public sealed record RepositoryRunView(
    string Id,
    string SourceId,
    long ConfigurationRevision,
    long Generation,
    string? Commit,
    string? RetryOfRunId,
    string Trigger,
    string? RequestedByUserId,
    RepositoryRunStatus Status,
    DateTime CreatedAtUtc,
    IReadOnlyList<RepositoryRunDiagnosticView> Diagnostics);

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryOperationStatus>))]
public enum RepositoryOperationStatus { Pending, Succeeded, Blocked }

public sealed record RepositoryOperationView(
    string Kind,
    string Name,
    string ResourceId,
    DeclarationPhase Phase,
    RepositoryOperationStatus Status,
    string? Blocker,
    bool Changed);

public sealed record RepositoryRunPage(
    IReadOnlyList<RepositoryRunView> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record RepositoryOperationPage(
    IReadOnlyList<RepositoryOperationView> Items,
    int Page,
    int PageSize,
    bool HasMore);
