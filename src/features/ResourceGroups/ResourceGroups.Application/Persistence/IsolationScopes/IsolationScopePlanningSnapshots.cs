using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

internal sealed class MongoIsolationScopePlanningSnapshots(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes)
{
    public async Task<ResolveNamedIsolationScopeOutcome> ResolveAsync(
        NamedIsolationScopeRequest request,
        CancellationToken cancellationToken)
    {
        var group = await groups.Find(ResourceGroupFilters.Active(request.TenantId, request.ResourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            return new IsolationScopePlanningUnavailable();

        var filter = request.Name is null
            ? IsolationScopeFilters.ByTenantResourceGroupAndId(
                request.TenantId, group.Id, group.DefaultIsolationScopeId)
            : IsolationScopeFilters.ByTenantResourceGroupAndNormalizedName(request.TenantId, group.Id, request.Name);
        var scope = await scopes.Find(filter).FirstOrDefaultAsync(cancellationToken);
        return Available(scope, group, request.Name is null)
            ? Snapshot(group, scope!)
            : new IsolationScopePlanningUnavailable();
    }

    public async Task<LoadIsolationScopePlanningSnapshotOutcome> ReadAsync(
        IsolationScopePlanningRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Planning snapshots require an active transaction.");

        var group = await groups.Find(session,
            ResourceGroupFilters.Active(request.TenantId, request.ResourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            return new IsolationScopePlanningUnavailable();

        var scope = await scopes.Find(session, IsolationScopeFilters.ByTenantResourceGroupAndId(
            request.TenantId, group.Id, request.IsolationScopeId ?? group.DefaultIsolationScopeId))
            .FirstOrDefaultAsync(cancellationToken);
        return Available(scope, group, request.IsolationScopeId is null)
            ? Snapshot(group, scope!)
            : new IsolationScopePlanningUnavailable();
    }

    public async Task<FenceIsolationScopePlanningSnapshotOutcome> FenceAsync(
        IsolationScopePlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var loaded = await ReadAsync(new(request.TenantId, request.ResourceGroupId, request.IsolationScopeId),
            session, cancellationToken);
        if (loaded is not IsolationScopePlanningSnapshot current || current != request)
            return new IsolationScopePlanningFenceRejected();

        // Every scope lifecycle mutation writes its parent aggregate; this also fences its absence/deletion.
        var written = await groups.UpdateOneAsync(session,
            ResourceGroupFilters.ActiveAtRevision(request.TenantId, request.ResourceGroupId,
                request.ResourceGroupRevision),
            Builders<ResourceGroup>.Update.Inc(group => group.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        return written.ModifiedCount == 1
            ? new IsolationScopePlanningFenced()
            : new IsolationScopePlanningFenceRejected();
    }

    private static bool Available(IsolationScope? scope, ResourceGroup group, bool requireDefault) =>
        scope is { Lifecycle: ActiveIsolationScopeLifecycleState } &&
        (!requireDefault || scope.IsDefault && scope.Id == group.DefaultIsolationScopeId);

    private static IsolationScopePlanningSnapshot Snapshot(ResourceGroup group, IsolationScope scope) =>
        new(group.TenantId, group.Id, group.Revision, scope.Id, scope.Revision, scope.Name, scope.IsDefault);
}
