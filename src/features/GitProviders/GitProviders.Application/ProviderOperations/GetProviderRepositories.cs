using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public union GetProviderRepositoriesOutcome(
    ProviderPage<ProviderRepository>,
    GitProviderRepositoryAccessError);

public delegate Task<GetProviderRepositoriesOutcome>
    GetProviderRepositories(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderRepositories(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderRepositories> logger)
{
    public async Task<GetProviderRepositoriesOutcome> InvokeAsync(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositoriesAsync(
                connection.ExternalInstallationId,
                page,
                pageSize,
                cancellationToken),
            logger);
        return result switch
        {
            ProviderPage<ProviderRepository> repositoryPage => repositoryPage,
            GitProviderRepositoryAccessError error => error
        };
    }
}
