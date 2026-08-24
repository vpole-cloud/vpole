namespace Pole.Features.GitProviders.Application.Common;

public sealed record GitHubIntegrationNotConfigured;

public sealed record GitProviderConnectionNotFound(string ConnectionId);

public sealed record GitProviderConnectionHasSourceRepositories(string ConnectionId);

public sealed record SourceRepositoryNotFound(string SourceRepositoryId);

public sealed record SourceRepositoryAlreadyLinked(
    string ConnectionId,
    string RepositoryId,
    string Branch,
    string CandidateKey);

public sealed record SourceRepositoryBuildPlanNotFound(string CandidateKey);

public sealed record SourceRepositorySelectedBuildPlanUnavailable(
    string SourceRepositoryId,
    string CandidateKey);

public sealed record SourceRepositoryHasBuildConfiguration(string SourceRepositoryId);

public sealed record RepositorySourceSnapshotTooLarge;

public sealed record GitProviderUnsupported(string Provider);

public sealed record GitProviderUnavailable(string Provider);

public union GitProviderRepositoryAccessError(
    GitProviderUnsupported,
    GitProviderUnavailable);
