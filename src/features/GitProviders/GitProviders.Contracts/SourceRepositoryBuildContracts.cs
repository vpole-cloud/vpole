using Pole.ResultType;

namespace Pole.Features.GitProviders.Contracts;

public enum SourceRepositoryBuildPlanKind
{
    Dockerfile,
    DotNet,
    Node
}

public sealed record SourceRepositoryBuildPlanSnapshot(
    string CandidateKey,
    SourceRepositoryBuildPlanKind Kind,
    string SourceRoot,
    string ManifestPath,
    IReadOnlyList<string> Commands,
    string? StartCommand,
    string Dockerfile);

public sealed record SourceRepositoryBuildSnapshot(
    string Id,
    string CandidateKey,
    string Branch,
    string CommitSha,
    SourceRepositoryBuildPlanSnapshot SelectedPlan);

public sealed record SourceRepositoryBuildNotFound(string SourceRepositoryId);

public sealed record SourceRepositoryBuildConnectionNotFound(
    string SourceRepositoryId,
    string ConnectionId);

public sealed record GitProviderUnsupported(string Provider);

public sealed record GitProviderUnavailable(string Provider);

public union GitProviderRepositoryAccessError(
    GitProviderUnsupported,
    GitProviderUnavailable);

public union FindSourceRepositoryBuildError(SourceRepositoryBuildNotFound);

public union DownloadSourceRepositoryArchiveError(
    SourceRepositoryBuildNotFound,
    SourceRepositoryBuildConnectionNotFound,
    GitProviderRepositoryAccessError);

public delegate Task<Result<SourceRepositoryBuildSnapshot, FindSourceRepositoryBuildError>>
    FindSourceRepositoryBuild(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

/// <summary>
/// Writes the exact requested commit to the caller-owned destination stream.
/// The destination remains owned by the caller and is not disposed by the provider.
/// </summary>
public delegate Task<Result<Unit, DownloadSourceRepositoryArchiveError>>
    DownloadSourceRepositoryArchive(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default);
