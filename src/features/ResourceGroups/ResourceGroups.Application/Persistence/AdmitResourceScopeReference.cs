using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence;

internal sealed class MongoAdmitResourceScopeReference(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IMongoCollection<ResourceRegistration> registrations)
{
    public async Task<Result<Unit, ResourceScopeReferenceUnavailable>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        ResourceScopeConsumerReference? consumer,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Reference admission requires a transaction.");

        var fence = await groups.UpdateOneAsync(
            session,
            ResourceGroupFilters.Active(tenantId, resourceGroupId),
            Builders<ResourceGroup>.Update.Inc(x => x.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        if (fence.ModifiedCount != 1)
            return new Err<ResourceScopeReferenceUnavailable>(new());

        var scope = await scopes
            .Find(
                session,
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                    tenantId,
                    resourceGroupId,
                    isolationScopeId))
            .FirstOrDefaultAsync(cancellationToken);
        if (scope?.Lifecycle is not ActiveIsolationScopeLifecycleState)
            return new Err<ResourceScopeReferenceUnavailable>(new());
        if (consumer is null)
            return new Ok<Unit>(Unit.Value);

        var exists = await registrations.Find(
            session,
            ResourceRegistrationFilters.Exact(
                tenantId,
                resourceGroupId,
                consumer.ResourceType,
                consumer.ResourceId)).AnyAsync(cancellationToken);
        return exists ? new Ok<Unit>(Unit.Value) : new Err<ResourceScopeReferenceUnavailable>(new());
    }
}
