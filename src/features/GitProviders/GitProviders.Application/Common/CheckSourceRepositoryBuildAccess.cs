using System.Net;
using System.Text.Json;
using Pole.Features.GitProviders.Application.Connections;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.GitProviders.Application.Common;

internal sealed class SourceRepositoryBuildAccessChecker(
    LoadSourceRepository loadRepository,
    LoadTenantIdentity loadTenant,
    LoadGitProviderConnection loadConnection,
    IGitHubAppClient client)
{
    public async Task<CheckSourceRepositoryBuildAccessOutcome> InvokeAsync(
        SourceRepositoryReferenceRequest request,
        CancellationToken cancellationToken)
    {
        var repository = await loadRepository(request.TenantId, request.SourceRepositoryId, cancellationToken);
        if (repository is null)
            return new SourceRepositoryBuildNotFound(request.SourceRepositoryId);
        if (await loadTenant(request.TenantId, cancellationToken) is TenantIdentityNotFound)
            return new SourceRepositoryAccessDenied();

        var connection = await loadConnection(request.TenantId, repository.ConnectionId, cancellationToken);
        if (connection is null)
            return new SourceRepositoryBuildConnectionNotFound(repository.Id, repository.ConnectionId);
        if (connection.Provider != GitProviderKind.GitHub)
            return new SourceRepositoryAccessDenied();

        try
        {
            await client.GetRepositoryAsync(connection.ExternalInstallationId, repository.ExternalRepositoryId,
                cancellationToken);
            return repository.ToBuildSnapshot();
        }
        catch (GitHubApiException exception) when (!exception.IsRateLimited &&
            (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
             exception.StatusCode == HttpStatusCode.NotFound && exception.Target == GitHubRequestTarget.Installation))
        {
            return new SourceRepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new SourceRepositoryRemoteNotFound();
        }
        catch (Exception exception) when (exception is GitHubApiException or HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new SourceRepositoryUnavailable();
        }
    }
}
