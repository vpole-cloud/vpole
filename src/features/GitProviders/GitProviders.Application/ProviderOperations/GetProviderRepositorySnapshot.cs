using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public delegate Task<Result<RepositorySourceSnapshot, GitProviderRepositoryAccessError>>
    GetProviderRepositorySnapshot(
        GitProviderConnection connection,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderRepositorySnapshot(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderRepositorySnapshot> logger)
{
    public Task<Result<RepositorySourceSnapshot, GitProviderRepositoryAccessError>> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default) =>
        GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositorySnapshotAsync(
                connection.ExternalInstallationId,
                repositoryId,
                branch,
                cancellationToken),
            logger);
}
