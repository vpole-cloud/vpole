// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryRunStatus>))]
public enum RepositoryRunStatus
{
    Queued, Resolving, Fetching, Applying, WaitingRetry, PendingCleanup, ConfigurationApplied,
    AlreadyCurrent, Blocked, Failed, Superseded, LifecycleCompleted, AwaitingConfirmation,
}

public sealed record RepositoryLocationView(
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory,
    string DisplayUrl
);

public sealed record RepositorySynchronizationView(
    long RequestedGeneration,
    long CompletedGeneration,
    string? ObservedCommit,
    string? AttemptedCommit,
    string? FullyAppliedCommit,
    string? CurrentRunId,
    string? LatestRunId
);

public sealed record RepositoryInventorySummary(
    long Owned,
    long PendingRemoval,
    long Revision
);

public sealed record RepositorySourceView(
    string Id,
    long Revision,
    RepositoryLocationView Repository,
    int PollIntervalSeconds,
    RepositoryRunStatus Status,
    RepositorySynchronizationView Synchronization,
    RepositoryInventorySummary Inventory,
    RepositoryRunView? LatestRun,
    GitSourceActivity Activity,
    GitPrunePolicy Prune
);

public sealed record RepositoryRunDiagnosticView(
    string Code,
    string? File,
    int? Document,
    string? Field
);

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
    IReadOnlyList<RepositoryRunDiagnosticView> Diagnostics,
    RepositoryOperationSummary? Operations,
    RepositoryRunLifecycleView? Lifecycle
);

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryOperationStatus>))]
public enum RepositoryOperationStatus { Pending, Succeeded, Blocked, PendingCleanup, AwaitingConfirmation }

public sealed record RepositoryOperationView(
    string Kind,
    string Name,
    string ResourceId,
    DeclarationPhase? Phase,
    RepositoryOperationStatus Status,
    string? Blocker,
    bool Changed,
    RepositoryOperationPlanView? Plan,
    RepositoryLifecycleOperationView? Lifecycle
);

public sealed record RepositoryOperationPlanView(
    GitPlannedAction Action,
    long Revision,
    DateTime EvaluatedAtUtc,
    IReadOnlyList<string> ChangedFields
);

public sealed record RepositoryOperationSummary(
    int Create,
    int Update,
    int NoOp,
    int Blocked,
    int Pending,
    RepositoryLifecycleOperationSummary? Lifecycle
);

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryLifecycleAuthorization>))]
public enum RepositoryLifecycleAuthorization { AwaitingConfirmation, UserConfirmed, PrunePolicy }

public sealed record RepositoryRunLifecycleView(
    string PreviewId,
    RepositoryLifecycleAuthorization Authorization
);

public sealed record RepositoryLifecycleOperationView(
    string ActionId,
    GitLifecycleAction Action,
    string? LastCompletedStep,
    string? ReplacementResourceId,
    string? ReplacementName,
    IReadOnlyList<string> ChangedFields
);

public sealed record RepositoryLifecycleOperationSummary(
    int Adopt,
    int Remove,
    int Replace,
    int Retain,
    int Detach,
    int PendingCleanup,
    int AwaitingConfirmation
);

public sealed record RepositoryRunPage(
    IReadOnlyList<RepositoryRunView> Items,
    int Page,
    int PageSize,
    bool HasMore
);

public sealed record RepositoryOperationPage(
    IReadOnlyList<RepositoryOperationView> Items,
    int Page,
    int PageSize,
    bool HasMore
);
