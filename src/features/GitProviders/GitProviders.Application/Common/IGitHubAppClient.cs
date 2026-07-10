namespace Pole.Features.GitProviders.Application.Common;

public sealed record GitHubInstallation(
    string Id,
    string AccountId,
    string AccountLogin,
    string AccountType,
    string RepositorySelection,
    IReadOnlyDictionary<string, string> Permissions);

public interface IGitHubAppClient
{
    Task<GitHubInstallation> GetInstallationAsync(string installationId, CancellationToken cancellationToken = default);
    Task<string> ExchangeOAuthCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default);
    Task<bool> UserCanAccessInstallationAsync(string userAccessToken, string installationId, CancellationToken cancellationToken = default);
    Task<ProviderPage<ProviderRepository>> GetRepositoriesAsync(string installationId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ProviderRepository> GetRepositoryAsync(string installationId, string repositoryId, CancellationToken cancellationToken = default);
    Task<ProviderPage<ProviderBranch>> GetBranchesAsync(string installationId, string repositoryId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<RepositorySourceSnapshot> GetRepositorySnapshotAsync(string installationId, string repositoryId, string branch, CancellationToken cancellationToken = default);
}

public sealed class GitHubApiException(
    string message,
    System.Net.HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
}
