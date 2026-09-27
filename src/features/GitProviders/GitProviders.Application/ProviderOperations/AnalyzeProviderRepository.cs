using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Contracts;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public union AnalyzeProviderRepositoryError(
    GitProviderConnectionNotFound,
    GitProviderRepositoryAccessError,
    RepositorySourceSnapshotTooLarge);

public union AnalyzeProviderRepositoryOutcome(
    AnalyzedProviderRepository,
    AnalyzeProviderRepositoryError);

public delegate Task<AnalyzeProviderRepositoryOutcome>
    AnalyzeProviderRepository(
        string tenantId,
        string connectionId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default);

internal sealed class GitProviderRepositoryAnalyzer(
    LoadGitProviderConnection loadGitProviderConnection,
    GetProviderRepositorySnapshot getProviderRepositorySnapshot,
    RepositorySourceAnalyzer analyzer)
{
    public async Task<AnalyzeProviderRepositoryOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var connection = await loadGitProviderConnection(tenantId, connectionId, cancellationToken);
        if (connection is null)
        {
            return new AnalyzeProviderRepositoryError(
                new GitProviderConnectionNotFound(connectionId));
        }

        var snapshotResult = await getProviderRepositorySnapshot(
            connection,
            repositoryId,
            branch.Trim(),
            cancellationToken);
        return snapshotResult switch
        {
            GitProviderRepositoryAccessError failure =>
                new AnalyzeProviderRepositoryError(failure),
            RepositorySourceSnapshot success =>
                analyzer.Analyze(success) switch
                {
                    Pole.Features.GitProviders.Domain.SourceRepositoryAnalysis analysis =>
                        new AnalyzedProviderRepository(
                                connection,
                                success.Repository,
                                analysis),
                    RepositorySourceSnapshotTooLarge tooLarge =>
                        new AnalyzeProviderRepositoryError(tooLarge)
                }
        };
    }
}
