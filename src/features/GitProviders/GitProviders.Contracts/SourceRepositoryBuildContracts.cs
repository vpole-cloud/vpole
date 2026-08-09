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

public interface ISourceRepositoryBuildAccess
{
    Task<SourceRepositoryBuildSnapshot?> FindAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the exact requested commit to the caller-owned destination stream.
    /// The destination remains owned by the caller and is not disposed by the provider.
    /// </summary>
    Task<Result> DownloadArchiveAsync(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default);
}
