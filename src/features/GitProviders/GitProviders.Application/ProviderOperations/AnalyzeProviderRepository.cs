using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Contracts;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public union AnalyzeProviderRepositoryError(
    GitProviderConnectionNotFound,
    GitProviderRepositoryAccessError,
    RepositorySourceSnapshotTooLarge);

public delegate Task<Result<AnalyzedProviderRepository, AnalyzeProviderRepositoryError>>
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
    public async Task<Result<AnalyzedProviderRepository, AnalyzeProviderRepositoryError>> InvokeAsync(
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
            return new Err<AnalyzeProviderRepositoryError>(
                new(new GitProviderConnectionNotFound(connectionId)));
        }

        var snapshotResult = await getProviderRepositorySnapshot(
            connection,
            repositoryId,
            branch.Trim(),
            cancellationToken);
        return snapshotResult switch
        {
            Err<GitProviderRepositoryAccessError> failure =>
                new Err<AnalyzeProviderRepositoryError>(new(failure.Value)),
            Ok<RepositorySourceSnapshot> success =>
                analyzer.Analyze(success.Value) switch
                {
                    Pole.Features.GitProviders.Domain.SourceRepositoryAnalysis analysis =>
                        new Ok<AnalyzedProviderRepository>(
                            new AnalyzedProviderRepository(
                                connection,
                                success.Value.Repository,
                                analysis)),
                    RepositorySourceSnapshotTooLarge tooLarge =>
                        new Err<AnalyzeProviderRepositoryError>(new(tooLarge))
                }
        };
    }
}
