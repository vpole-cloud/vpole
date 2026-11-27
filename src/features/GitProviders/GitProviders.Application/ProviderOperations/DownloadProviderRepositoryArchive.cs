// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public sealed record ProviderRepositoryArchiveDownloaded;

public union DownloadProviderRepositoryArchiveOutcome(
    ProviderRepositoryArchiveDownloaded,
    GitProviderRepositoryAccessError);

public delegate Task<DownloadProviderRepositoryArchiveOutcome> DownloadProviderRepositoryArchive(
    GitProviderConnection connection,
    string repositoryId,
    string commitSha,
    Stream destination,
    CancellationToken cancellationToken = default);

internal sealed class GitHubDownloadProviderRepositoryArchive(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubDownloadProviderRepositoryArchive> logger
)
{
    public async Task<DownloadProviderRepositoryArchiveOutcome> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (connection.Provider is not GitProviderKind.GitHub)
            return new GitProviderRepositoryAccessError(new GitProviderUnsupported(connection.Provider.ToString()));

        try
        {
            await gitHubClient.DownloadRepositoryArchiveAsync(
                connection.ExternalInstallationId,
                repositoryId,
                commitSha,
                destination,
                cancellationToken);
            return new ProviderRepositoryArchiveDownloaded();
        }
        catch (GitHubApiException exception)
        {
            logger.LogInformation(
                exception,
                "GitHub rejected a repository archive download with status {StatusCode}.",
                exception.StatusCode);
            return new GitProviderRepositoryAccessError(new GitProviderUnavailable("GitHub"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "GitHub repository archive download failed.");
            return new GitProviderRepositoryAccessError(new GitProviderUnavailable("GitHub"));
        }
    }
}
