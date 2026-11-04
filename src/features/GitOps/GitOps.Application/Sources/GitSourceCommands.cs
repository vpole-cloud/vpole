using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Sources;

public sealed record CreateGitRepositorySource(
    string TenantId,
    string CreationKey,
    GitRepositoryLocation Repository,
    GitSyncPolicy Sync,
    GitSourceAudit Audit,
    string DisplayUrl,
    GitPrunePolicy Prune = GitPrunePolicy.Retain);

public sealed record GitSourceCommand(
    string TenantId,
    string SourceId,
    long ExpectedRevision,
    string RequestKey,
    GitRunInitiator Initiator);

public sealed record RetryGitSourceCommand(
    GitSourceCommand Source,
    string RunId);

public sealed record ChangeGitSourcePolicy(
    string TenantId,
    string SourceId,
    long ExpectedRevision,
    GitSyncPolicy Sync,
    GitPrunePolicy Prune = GitPrunePolicy.Retain);

public sealed record GitSourceNotFound;

public sealed record GitSourceCreationDeleted;

public sealed record GitSourceInactive(GitSourceActivity Activity);

public sealed record GitSourceRevisionConflict;

public sealed record GitSourceIdempotencyConflict;

public sealed record GitSourceBusy;

public sealed record GitRetryNotAvailable;

public sealed record GitSourceScheduled(
    string SourceId,
    long Generation);

public union CreateGitRepositorySourceOutcome(
    RepositorySource,
    GitSourceIdempotencyConflict,
    GitSourceCreationDeleted);

public union RequestGitSourceSyncOutcome(
    GitSourceScheduled,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceIdempotencyConflict,
    GitSourceInactive);

public union RetryGitSourceOutcome(
    GitSourceScheduled,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceIdempotencyConflict,
    GitSourceBusy,
    GitRetryNotAvailable,
    GitSourceInactive);

public union ChangeGitSourcePolicyOutcome(
    RepositorySource,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceInactive);
