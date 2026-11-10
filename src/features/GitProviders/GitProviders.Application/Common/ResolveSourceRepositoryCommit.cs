using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Contracts;

namespace Pole.Features.GitProviders.Application.Common;

internal sealed class SourceRepositoryCommitResolver(
    LoadSourceRepository loadRepository,
    ResolveProviderRepository resolveRepository
)
{
    public async Task<ResolveSourceRepositoryCommitOutcome> InvokeAsync(
        SourceRepositoryReferenceRequest request,
        CancellationToken cancellationToken)
    {
        var repository = await loadRepository(request.TenantId, request.SourceRepositoryId, cancellationToken);
        if (repository is null)
            return new SourceRepositoryBuildNotFound(request.SourceRepositoryId);

        var result = await resolveRepository(
            new ResolveProviderRepositoryRequest(
                request.TenantId,
                repository.ConnectionId,
                repository.ExternalRepositoryId,
                repository.Branch),
            cancellationToken);
        return result switch
        {
            ResolvedProviderRepository resolved => new SourceRepositoryCommitResolved(
                repository.ToBuildSnapshot(),
                resolved.CommitSha),
            GitProviderConnectionNotFound missing => new SourceRepositoryBuildConnectionNotFound(
                repository.Id,
                missing.ConnectionId),
            SourceRepositoryRemoteNotFound missing => missing,
            SourceRepositoryBranchNotFound missing => missing,
            SourceRepositoryAccessDenied denied => denied,
            SourceRepositoryUnavailable unavailable => unavailable,
        };
    }
}
