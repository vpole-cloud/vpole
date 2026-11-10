using System.Net;
using System.Text.Json;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Connections;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public sealed record ResolveProviderRepositoryRequest(
    string TenantId,
    string ConnectionId,
    string RepositoryId,
    string Branch
);

public sealed record ResolvedProviderRepository(
    ProviderRepository Repository,
    string CommitSha
);

public union ResolveProviderRepositoryOutcome(
    ResolvedProviderRepository,
    GitProviderConnectionNotFound,
    SourceRepositoryRemoteNotFound,
    SourceRepositoryBranchNotFound,
    SourceRepositoryAccessDenied,
    SourceRepositoryUnavailable);

public delegate Task<ResolveProviderRepositoryOutcome> ResolveProviderRepository(
    ResolveProviderRepositoryRequest request,
    CancellationToken cancellationToken);

internal sealed class GitHubResolveProviderRepository(
    LoadTenantIdentity loadTenant,
    LoadGitProviderConnection loadConnection,
    IGitHubAppClient client
)
{
    public async Task<ResolveProviderRepositoryOutcome> InvokeAsync(
        ResolveProviderRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        if (await loadTenant(request.TenantId, cancellationToken) is TenantIdentityNotFound)
            return new SourceRepositoryAccessDenied();

        var connection = await loadConnection(request.TenantId, request.ConnectionId, cancellationToken);
        if (connection is null)
            return new GitProviderConnectionNotFound(request.ConnectionId);

        if (connection.Provider != GitProviderKind.GitHub)
            return new SourceRepositoryAccessDenied();

        ProviderRepository repository;
        try
        {
            repository = await client.GetRepositoryAsync(
                connection.ExternalInstallationId,
                request.RepositoryId,
                cancellationToken);
        }
        catch (GitHubApiException exception) when (AccessDenied(exception))
        {
            return new SourceRepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new SourceRepositoryRemoteNotFound();
        }
        catch (Exception exception) when (Temporary(exception, cancellationToken))
        {
            return new SourceRepositoryUnavailable();
        }

        try
        {
            var commit = await client.ResolveCommitAsync(
                new GitHubResolveCommitRequest(
                    new GitHubRepositoryAccess(connection.ExternalInstallationId, repository),
                    request.Branch),
                cancellationToken);
            return commit is { Length: 40 } && commit.All(char.IsAsciiHexDigit)
                ? new ResolvedProviderRepository(repository, commit.ToLowerInvariant())
                : new SourceRepositoryUnavailable();
        }
        catch (GitHubApiException exception) when (AccessDenied(exception))
        {
            return new SourceRepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new SourceRepositoryBranchNotFound(request.Branch);
        }
        catch (Exception exception) when (Temporary(exception, cancellationToken))
        {
            return new SourceRepositoryUnavailable();
        }
    }

    private static bool AccessDenied(GitHubApiException exception) => !exception.IsRateLimited &&
        (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            (exception.StatusCode == HttpStatusCode.NotFound && exception.Target == GitHubRequestTarget.Installation));

    private static bool Temporary(Exception exception, CancellationToken cancellationToken) =>
        exception is GitHubApiException or HttpRequestException or JsonException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
