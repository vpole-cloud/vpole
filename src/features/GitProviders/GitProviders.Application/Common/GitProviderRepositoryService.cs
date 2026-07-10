using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Common;

public interface IGitProviderRepositoryService
{
    Task<Result<ProviderPage<ProviderRepository>>> GetRepositoriesAsync(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<ProviderPage<ProviderBranch>>> GetBranchesAsync(
        GitProviderConnection connection,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<RepositorySourceSnapshot>> GetRepositorySnapshotAsync(
        GitProviderConnection connection,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default);
}

public sealed class GitProviderRepositoryService(IGitHubAppClient gitHubClient)
    : IGitProviderRepositoryService
{
    public Task<Result<ProviderPage<ProviderRepository>>> GetRepositoriesAsync(
        GitProviderConnection connection,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositoriesAsync(
                connection.ExternalInstallationId,
                page,
                pageSize,
                cancellationToken));

    public Task<Result<ProviderPage<ProviderBranch>>> GetBranchesAsync(
        GitProviderConnection connection,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            connection,
            () => gitHubClient.GetBranchesAsync(
                connection.ExternalInstallationId,
                repositoryId,
                page,
                pageSize,
                cancellationToken));

    public Task<Result<RepositorySourceSnapshot>> GetRepositorySnapshotAsync(
        GitProviderConnection connection,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            connection,
            () => gitHubClient.GetRepositorySnapshotAsync(
                connection.ExternalInstallationId,
                repositoryId,
                branch,
                cancellationToken));

    private static async Task<Result<T>> ExecuteAsync<T>(
        GitProviderConnection connection,
        Func<Task<T>> operation)
        where T : notnull
    {
        if (connection.Provider != GitProviderKind.GitHub)
        {
            return Failures.Internal(
                "The git provider connection is not supported.",
                $"Provider: {connection.Provider}");
        }

        try
        {
            return await operation();
        }
        catch (GitHubApiException exception)
        {
            return Failures.ExternalService(
                "GitHub could not complete the repository request.",
                "GitHub",
                exception.StatusCode is null
                    ? "Network request failed."
                    : $"Status {(int)exception.StatusCode.Value}.");
        }
    }
}
