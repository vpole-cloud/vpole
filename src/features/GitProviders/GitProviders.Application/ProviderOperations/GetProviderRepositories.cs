using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public delegate Task<Result<ProviderPage<ProviderRepository>, GitProviderRepositoryAccessError>>
    GetProviderRepositories(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderRepositories(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderRepositories> logger)
{
    public Task<Result<ProviderPage<ProviderRepository>, GitProviderRepositoryAccessError>> InvokeAsync(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositoriesAsync(
                connection.ExternalInstallationId,
                page,
                pageSize,
                cancellationToken),
            logger);
}
