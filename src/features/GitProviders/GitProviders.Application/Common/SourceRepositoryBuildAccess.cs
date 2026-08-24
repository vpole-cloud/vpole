using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Common;

public sealed class SourceRepositoryBuildAccess(
    LoadSourceRepository loadSourceRepository,
    LoadGitProviderConnection loadGitProviderConnection,
    DownloadProviderRepositoryArchive downloadProviderRepositoryArchive)
    : ISourceRepositoryBuildAccess
{
    public async Task<SourceRepositoryBuildSnapshot?> FindAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(tenantId, sourceRepositoryId, cancellationToken);
        return repository?.ToBuildSnapshot();
    }

    public async Task<Result> DownloadArchiveAsync(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(tenantId, sourceRepositoryId, cancellationToken);
        if (repository is null)
            return Failures.NotFound("Source repository not found.", sourceRepositoryId);

        var connection = await loadGitProviderConnection(
            tenantId,
            repository.ConnectionId,
            cancellationToken);
        if (connection is null)
            return Failures.Conflict("The linked git provider connection no longer exists.");

        var downloaded = await downloadProviderRepositoryArchive(
            connection,
            repository.ExternalRepositoryId,
            commitSha,
            destination,
            cancellationToken);
        return downloaded switch
        {
            Ok<Unit> => Result.Success(),
            Err<GitProviderRepositoryAccessError> error => MapLegacyFailure(error.Value)
        };
    }

    // SourceBuilds has not yet migrated to typed provider outcomes. This is its
    // temporary compatibility translation; the ledger records its removal point.
    private static Result MapLegacyFailure(GitProviderRepositoryAccessError error) => error switch
    {
        GitProviderUnsupported unsupported => Failures.Internal(
            "The git provider connection is not supported.",
            $"Provider: {unsupported.Provider}"),
        GitProviderUnavailable unavailable => Failures.ExternalService(
            "GitHub could not complete the repository request.",
            unavailable.Provider,
            "GitHub repository operation is unavailable.")
    };
}

internal static class SourceRepositoryBuildMappings
{
    public static SourceRepositoryBuildSnapshot ToBuildSnapshot(this SourceRepository repository) =>
        new(
            repository.Id,
            repository.CandidateKey,
            repository.Branch,
            repository.CommitSha,
            new SourceRepositoryBuildPlanSnapshot(
                repository.SelectedPlan.CandidateKey,
                repository.SelectedPlan.Kind switch
                {
                    SourceBuildPlanKind.Dockerfile => SourceRepositoryBuildPlanKind.Dockerfile,
                    SourceBuildPlanKind.DotNet => SourceRepositoryBuildPlanKind.DotNet,
                    SourceBuildPlanKind.Node => SourceRepositoryBuildPlanKind.Node,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(repository),
                        repository.SelectedPlan.Kind,
                        "Unsupported source build plan kind.")
                },
                repository.SelectedPlan.SourceRoot,
                repository.SelectedPlan.ManifestPath,
                repository.SelectedPlan.Commands.ToArray(),
                repository.SelectedPlan.StartCommand,
                repository.SelectedPlan.DockerfilePreview));
}
