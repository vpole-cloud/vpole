// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace Pole.Features.GitProviders.Application.Common;

public sealed record GitHubInstallation(
    string Id,
    string AccountId,
    string AccountLogin,
    string AccountType,
    string RepositorySelection,
    IReadOnlyDictionary<string, string> Permissions
);

public interface IGitHubAppClient
{
    Task<GitHubInstallation> GetInstallationAsync(string installationId, CancellationToken cancellationToken = default);

    Task<string> ExchangeOAuthCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    Task<bool> UserCanAccessInstallationAsync(
        string userAccessToken,
        string installationId,
        CancellationToken cancellationToken = default);

    Task<ProviderPage<ProviderRepository>> GetRepositoriesAsync(
        string installationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ProviderRepository> GetRepositoryAsync(
        string installationId,
        string repositoryId,
        CancellationToken cancellationToken = default);

    Task<ProviderPage<ProviderBranch>> GetBranchesAsync(
        string installationId,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<RepositorySourceSnapshot> GetRepositorySnapshotAsync(
        string installationId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default);

    Task<string> ResolveCommitAsync(GitHubResolveCommitRequest request, CancellationToken cancellationToken);
    Task<GitHubCommitTree> ReadCommitTreeAsync(GitHubReadTreeRequest request, CancellationToken cancellationToken);

    Task DownloadRepositoryArchiveAsync(
        string installationId,
        string repositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubApiException(
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null
) : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public GitHubRequestTarget Target { get; init; }
    public bool IsRateLimited { get; init; }
}

public enum GitHubRequestTarget { Other, Installation, Repository, Ref }

public sealed record GitHubRepositoryAccess(
    string InstallationId,
    ProviderRepository Repository
);

public sealed record GitHubResolveCommitRequest(
    GitHubRepositoryAccess Access,
    string Branch
);

public sealed record GitHubReadTreeRequest(
    GitHubRepositoryAccess Access,
    string Commit
);

public sealed record GitHubTreeEntry(
    string Path,
    string Type,
    string Mode
);

public sealed record GitHubCommitTree(
    IReadOnlyList<GitHubTreeEntry> Entries,
    bool Truncated
);

public sealed class GitHubUnsafeRedirectException : Exception
{
    public GitHubUnsafeRedirectException() : base("GitHub returned an unsupported archive redirect.")
    {
    }
}
