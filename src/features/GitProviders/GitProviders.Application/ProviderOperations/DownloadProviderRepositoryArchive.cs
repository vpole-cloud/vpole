using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public delegate Task<Result<Unit, GitProviderRepositoryAccessError>> DownloadProviderRepositoryArchive(
    GitProviderConnection connection,
    string repositoryId,
    string commitSha,
    Stream destination,
    CancellationToken cancellationToken = default);

internal sealed class GitHubDownloadProviderRepositoryArchive(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubDownloadProviderRepositoryArchive> logger)
{
    public async Task<Result<Unit, GitProviderRepositoryAccessError>> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (connection.Provider is not GitProviderKind.GitHub)
        {
            return new Err<GitProviderRepositoryAccessError>(
                new(new GitProviderUnsupported(connection.Provider.ToString())));
        }

        try
        {
            await gitHubClient.DownloadRepositoryArchiveAsync(
                connection.ExternalInstallationId,
                repositoryId,
                commitSha,
                destination,
                cancellationToken);
            return new Ok<Unit>(Unit.Value);
        }
        catch (GitHubApiException exception)
        {
            logger.LogInformation(
                exception,
                "GitHub rejected a repository archive download with status {StatusCode}.",
                exception.StatusCode);
            return new Err<GitProviderRepositoryAccessError>(
                new(new GitProviderUnavailable("GitHub")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "GitHub repository archive download failed.");
            return new Err<GitProviderRepositoryAccessError>(
                new(new GitProviderUnavailable("GitHub")));
        }
    }
}
