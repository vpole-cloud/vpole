using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed partial class DeclarativeRedisProvider
{
    public async Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Lifecycle warning reads require an active transaction.");

        if (request.Action is not (GitLifecycleAction.Remove or GitLifecycleAction.Replace) ||
            !await resources.Find(session, RedisFilters.ById(request.TenantId, request.ResourceId))
                .AnyAsync(cancellationToken))
            return [];

        // RedisInKubernetes requests native PVC deletion through StatefulSet WhenDeleted=Delete.
        return ["RedisPersistentVolumeClaimsDeleted", "PersistentVolumeReclaimPolicyDeterminesDataRetention"];
    }
}
