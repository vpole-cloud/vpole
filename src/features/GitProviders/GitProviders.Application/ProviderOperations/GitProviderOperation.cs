using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

internal union GitProviderOperationOutcome<T>(T, GitProviderRepositoryAccessError)
    where T : notnull;

internal static class GitProviderOperation
{
    internal static async Task<GitProviderOperationOutcome<T>> ExecuteAsync<T>(
        GitProviderConnection connection,
        Func<Task<T>> operation,
        ILogger logger)
        where T : notnull
    {
        if (connection.Provider is not GitProviderKind.GitHub)
            return new GitProviderRepositoryAccessError(new GitProviderUnsupported(connection.Provider.ToString()));

        try
        {
            return await operation();
        }
        catch (GitHubApiException exception)
        {
            logger.LogInformation(
                exception,
                "GitHub rejected a repository operation with status {StatusCode}.",
                exception.StatusCode);
            return new GitProviderRepositoryAccessError(new GitProviderUnavailable("GitHub"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "GitHub repository operation failed before the provider request completed.");
            return new GitProviderRepositoryAccessError(new GitProviderUnavailable("GitHub"));
        }
    }
}
