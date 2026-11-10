using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public union GetProviderRepositorySnapshotOutcome(RepositorySourceSnapshot, GitProviderRepositoryAccessError);

public delegate Task<GetProviderRepositorySnapshotOutcome> GetProviderRepositorySnapshot(
    GitProviderConnection connection,
    string repositoryId,
    string branch,
    CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderRepositorySnapshot(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderRepositorySnapshot> logger
)
{
    public async Task<GetProviderRepositorySnapshotOutcome> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var result = await GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositorySnapshotAsync(
                connection.ExternalInstallationId,
                repositoryId,
                branch,
                cancellationToken),
            logger);
        return result switch
        {
            RepositorySourceSnapshot snapshot => snapshot,
            GitProviderRepositoryAccessError error => error,
        };
    }
}
