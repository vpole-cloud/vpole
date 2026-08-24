using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public delegate Task<Result<ProviderPage<ProviderBranch>, GitProviderRepositoryAccessError>>
    GetProviderBranches(
        GitProviderConnection connection,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderBranches(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderBranches> logger)
{
    public Task<Result<ProviderPage<ProviderBranch>, GitProviderRepositoryAccessError>> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetBranchesAsync(
                connection.ExternalInstallationId,
                repositoryId,
                page,
                pageSize,
                cancellationToken),
            logger);
}
