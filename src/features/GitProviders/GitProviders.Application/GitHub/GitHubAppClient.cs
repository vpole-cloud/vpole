using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Pole.Features.GitProviders.Application.Common;

namespace Pole.Features.GitProviders.Application.GitHub;

public sealed class GitHubAppClient(
    HttpClient httpClient,
    GitHubAppOptions options,
    CreateGitHubAppJwt createJwt,
    IMemoryCache cache,
    TimeProvider timeProvider) : IGitHubAppClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GitHubInstallation> GetInstallationAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, ApiUrl($"/app/installations/{Escape(installationId)}"), createJwt());
        var response = await SendAsync<InstallationResponse>(request, cancellationToken);

        return new GitHubInstallation(
            response.Id.ToString(),
            response.Account.Id.ToString(),
            response.Account.Login,
            response.Account.Type,
            response.RepositorySelection,
            response.Permissions);
    }

    public async Task<string> ExchangeOAuthCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebUrl("/login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = options.OAuthCallbackUrl,
                ["code_verifier"] = codeVerifier
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SendAsync<OAuthTokenResponse>(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(response.AccessToken))
            throw new GitHubApiException("GitHub did not return a user access token.");

        return response.AccessToken;
    }

    public async Task<bool> UserCanAccessInstallationAsync(
        string userAccessToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/user/installations/{Escape(installationId)}/repositories?per_page=1"),
            userAccessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            return false;

        throw CreateException(response.StatusCode);
    }

    public async Task<ProviderPage<ProviderRepository>> GetRepositoriesAsync(
        string installationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var token = await GetInstallationTokenAsync(installationId, cancellationToken);
        using var request = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/installation/repositories?page={page}&per_page={pageSize}"),
            token);
        var response = await SendAsync<RepositoriesResponse>(request, cancellationToken);
        var repositories = response.Repositories.Select(MapRepository).ToArray();
        return new ProviderPage<ProviderRepository>(repositories, page, pageSize, page * pageSize < response.TotalCount);
    }

    public async Task<ProviderRepository> GetRepositoryAsync(
        string installationId,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var token = await GetInstallationTokenAsync(installationId, cancellationToken);
        using var request = CreateRequest(HttpMethod.Get, ApiUrl($"/repositories/{Escape(repositoryId)}"), token);
        return MapRepository(await SendAsync<RepositoryResponse>(request, cancellationToken));
    }

    public async Task<ProviderPage<ProviderBranch>> GetBranchesAsync(
        string installationId,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var repository = await GetRepositoryAsync(installationId, repositoryId, cancellationToken);
        var token = await GetInstallationTokenAsync(installationId, cancellationToken);
        using var request = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/repos/{EscapePath(repository.FullName)}/branches?page={page}&per_page={pageSize}"),
            token);
        var response = await SendAsync<BranchResponse[]>(request, cancellationToken);
        var branches = response
            .Select(branch => new ProviderBranch(branch.Name, branch.Commit.Sha, branch.Protected))
            .ToArray();
        return new ProviderPage<ProviderBranch>(branches, page, pageSize, branches.Length == pageSize);
    }

    public async Task<RepositorySourceSnapshot> GetRepositorySnapshotAsync(
        string installationId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var repository = await GetRepositoryAsync(installationId, repositoryId, cancellationToken);
        var token = await GetInstallationTokenAsync(installationId, cancellationToken);
        var repositoryPath = EscapePath(repository.FullName);

        using var branchRequest = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/repos/{repositoryPath}/branches/{Escape(branch)}"),
            token);
        var branchResponse = await SendAsync<BranchResponse>(branchRequest, cancellationToken);

        using var commitRequest = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/repos/{repositoryPath}/git/commits/{Escape(branchResponse.Commit.Sha)}"),
            token);
        var commitResponse = await SendAsync<GitCommitResponse>(commitRequest, cancellationToken);

        using var treeRequest = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/repos/{repositoryPath}/git/trees/{Escape(commitResponse.Tree.Sha)}?recursive=1"),
            token);
        var treeResponse = await SendAsync<GitTreeResponse>(treeRequest, cancellationToken);
        var paths = treeResponse.Tree
            .Where(item => string.Equals(item.Type, "blob", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        var relevantPaths = paths.Where(IsContentRequired).Take(100).ToArray();
        var contentBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relevantPaths)
        {
            using var contentRequest = CreateRequest(
                HttpMethod.Get,
                ApiUrl($"/repos/{repositoryPath}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(branch)}"),
                token);
            var contentResponse = await SendAsync<ContentResponse>(contentRequest, cancellationToken);
            if (string.Equals(contentResponse.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                var compact = contentResponse.Content.Replace("\n", string.Empty, StringComparison.Ordinal);
                contentBuilder[path] = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
            }
        }

        return new RepositorySourceSnapshot(
            repository,
            branch,
            branchResponse.Commit.Sha,
            paths,
            contentBuilder.ToImmutable(),
            treeResponse.Truncated);
    }

    public async Task DownloadRepositoryArchiveAsync(
        string installationId,
        string repositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var repository = await GetRepositoryAsync(installationId, repositoryId, cancellationToken);
        var token = await GetInstallationTokenAsync(installationId, cancellationToken);
        using var request = CreateRequest(
            HttpMethod.Get,
            ApiUrl($"/repos/{EscapePath(repository.FullName)}/tarball/{Escape(commitSha)}"),
            token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubApiException("GitHub could not be reached.", innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw CreateException(response.StatusCode);
            await response.Content.CopyToAsync(destination, cancellationToken);
        }
    }

    private async Task<string> GetInstallationTokenAsync(string installationId, CancellationToken cancellationToken)
    {
        var cacheKey = $"github-installation-token:{installationId}";
        if (cache.TryGetValue(cacheKey, out InstallationTokenCacheEntry? cached) &&
            cached is not null && cached.ExpiresAtUtc > timeProvider.GetUtcNow().AddMinutes(5))
        {
            return cached.Token;
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            ApiUrl($"/app/installations/{Escape(installationId)}/access_tokens"),
            createJwt());
        request.Content = JsonContent.Create(new { permissions = new { contents = "read" } });
        var response = await SendAsync<InstallationTokenResponse>(request, cancellationToken);
        var entry = new InstallationTokenCacheEntry(response.Token, response.ExpiresAt);
        cache.Set(cacheKey, entry, response.ExpiresAt.AddMinutes(-5));
        return entry.Token;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubApiException("GitHub could not be reached.", innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw CreateException(response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return result ?? throw new GitHubApiException("GitHub returned an empty response.", response.StatusCode);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", options.ApiVersion);
        return request;
    }

    private string ApiUrl(string path) => $"{options.ApiBaseUrl.TrimEnd('/')}{path}";
    private string WebUrl(string path) => $"{options.WebBaseUrl.TrimEnd('/')}{path}";

    private static GitHubApiException CreateException(HttpStatusCode statusCode) =>
        new($"GitHub request failed with status {(int)statusCode}.", statusCode);

    private static ProviderRepository MapRepository(RepositoryResponse repository) =>
        new(
            repository.Id.ToString(),
            repository.Name,
            repository.FullName,
            repository.HtmlUrl,
            repository.Private,
            repository.DefaultBranch,
            repository.UpdatedAt);

    private static bool IsContentRequired(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private sealed record InstallationTokenCacheEntry(string Token, DateTimeOffset ExpiresAtUtc);

    private sealed record InstallationResponse(
        long Id,
        AccountResponse Account,
        [property: JsonPropertyName("repository_selection")] string RepositorySelection,
        Dictionary<string, string> Permissions);

    private sealed record AccountResponse(long Id, string Login, string Type);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record InstallationTokenResponse(
        string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    private sealed record RepositoriesResponse(
        [property: JsonPropertyName("total_count")] int TotalCount,
        RepositoryResponse[] Repositories);

    private sealed record RepositoryResponse(
        long Id,
        string Name,
        [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        bool Private,
        [property: JsonPropertyName("default_branch")] string DefaultBranch,
        [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt);

    private sealed record BranchResponse(string Name, CommitReference Commit, bool Protected);
    private sealed record CommitReference(string Sha);
    private sealed record GitCommitResponse(GitTreeReference Tree);
    private sealed record GitTreeReference(string Sha);
    private sealed record GitTreeResponse(GitTreeItem[] Tree, bool Truncated);
    private sealed record GitTreeItem(string Path, string Type);
    private sealed record ContentResponse(string Content, string Encoding);
}
