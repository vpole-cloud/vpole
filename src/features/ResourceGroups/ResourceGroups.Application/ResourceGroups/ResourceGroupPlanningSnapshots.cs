using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

internal sealed class MongoResourceGroupPlanningSnapshots(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes)
{
    public async Task<LoadResourceGroupPlanningSnapshotOutcome> ReadAsync(
        ResourceGroupPlanningRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var group = await groups.Find(session,
            ResourceGroupFilters.Active(request.TenantId, request.ResourceGroupId)).FirstOrDefaultAsync(ct);
        if (group is null)
            return new ResourceGroupPlanningUnavailable();

        var scope = await scopes.Find(session, IsolationScopeFilters.ByTenantResourceGroupAndId(
            request.TenantId, group.Id, group.DefaultIsolationScopeId)).FirstOrDefaultAsync(ct);
        if (scope is not { IsDefault: true, Lifecycle: ActiveIsolationScopeLifecycleState })
            return new ResourceGroupPlanningUnavailable();

        return new ResourceGroupPlanningSnapshot(group.TenantId, group.Id, group.Revision, scope.Id, scope.Revision);
    }

    public async Task<FenceResourceGroupPlanningSnapshotOutcome> FenceAsync(
        ResourceGroupPlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadAsync(new(request.TenantId, request.ResourceGroupId), session, ct);
        if (read is not ResourceGroupPlanningSnapshot current || current != request)
            return new ResourceGroupPlanningFenceRejected();

        // Default-scope lifecycle changes are serialized through its parent group.
        var result = await groups.UpdateOneAsync(session,
            ResourceGroupFilters.ActiveAtRevision(request.TenantId, request.ResourceGroupId, request.Revision),
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1), cancellationToken: ct);
        return result.ModifiedCount == 1
            ? new ResourceGroupPlanningFenced()
            : new ResourceGroupPlanningFenceRejected();
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Planning snapshots require an active transaction.");
    }
}
