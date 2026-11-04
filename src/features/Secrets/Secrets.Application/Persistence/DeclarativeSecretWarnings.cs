using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Lifecycle warning reads require an active transaction.");

        if (request.Action is not (GitLifecycleAction.Remove or GitLifecycleAction.Replace) ||
            !await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
                .AnyAsync(cancellationToken))
            return [];

        return request.Action == GitLifecycleAction.Replace
            ? ["SecretStoredVersionsDeleted", "ReplacementSecretValuesRequired"]
            : ["SecretStoredVersionsDeleted"];
    }
}
