using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;

internal sealed class MongoResourceRegistrationMutations(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<ResourceRegistration> registrations,
    TimeProvider timeProvider)
{
    public async Task<Result<RegisteredResource, ResourceRegistrationMutationError>> RegisterAsync(
        string? owningPluginId,
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(resource);
        var candidate = ResourceRegistrationFactory.Create(
            tenantId,
            resourceGroupId,
            resource.ResourceTypeId,
            resource.ResourceId,
            resource.DisplayName,
            owningPluginId,
            new ResourceRegistrationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));

        var existing = await registrations
            .Find(
                session,
                ResourceRegistrationFilters.ByIdentity(
                    candidate.TenantId,
                    resource.ResourceTypeId,
                    candidate.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return IsSameRegistration(existing, candidate)
                ? new Ok<RegisteredResource>(ToContract(existing))
                : Conflict(resource);
        }

        var admission = await AdvanceFenceAsync(
            tenantId,
            resourceGroupId,
            session,
            cancellationToken);
        if (admission is { } admissionError)
            return new Err<ResourceRegistrationMutationError>(admissionError);

        try
        {
            await registrations.InsertOneAsync(
                session,
                candidate,
                cancellationToken: cancellationToken);
            return new Ok<RegisteredResource>(ToContract(candidate));
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(resource);
        }
    }

    public async Task<Result<RegisteredResource, ResourceRegistrationMutationError>> UpdateAsync(
        string? owningPluginId,
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(resource);
        var current = await registrations
            .Find(
                session,
                ResourceRegistrationFilters.ByIdentity(
                    tenantId,
                    resource.ResourceTypeId,
                    resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new Err<ResourceRegistrationMutationError>(
                new ResourceRegistrationMutationError(
                    new ResourceRegistrationNotFound(
                        resource.ResourceTypeId,
                        resource.ResourceId)));
        }
        if (!string.Equals(current.ResourceGroupId, resourceGroupId, StringComparison.Ordinal) ||
            !string.Equals(current.OwningPluginId, owningPluginId, StringComparison.Ordinal))
        {
            return Conflict(resource);
        }

        var replacement = ResourceRegistrationFactory.UpdateDisplayName(
            current,
            resource.DisplayName,
            timeProvider.GetUtcNow().UtcDateTime);
        if (ReferenceEquals(replacement, current))
            return new Ok<RegisteredResource>(ToContract(current));

        var admission = await AdvanceFenceAsync(
            tenantId,
            resourceGroupId,
            session,
            cancellationToken);
        if (admission is { } admissionError)
            return new Err<ResourceRegistrationMutationError>(admissionError);

        var write = await registrations.ReplaceOneAsync(
            session,
            ResourceRegistrationFilters.Exact(
                tenantId,
                resourceGroupId,
                resource.ResourceTypeId,
                resource.ResourceId),
            replacement,
            cancellationToken: cancellationToken);
        return write.ModifiedCount == 1
            ? new Ok<RegisteredResource>(ToContract(replacement))
            : Conflict(resource);
    }

    public async Task<Result<Unit, ResourceRegistrationMutationError>> UnregisterAsync(
        string? owningPluginId,
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var current = await registrations
            .Find(
                session,
                ResourceRegistrationFilters.ByIdentity(tenantId, resourceTypeId, resourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new Ok<Unit>(Unit.Value);
        if (!string.Equals(current.ResourceGroupId, resourceGroupId, StringComparison.Ordinal) ||
            !string.Equals(current.OwningPluginId, owningPluginId, StringComparison.Ordinal))
        {
            return new Err<ResourceRegistrationMutationError>(
                new ResourceRegistrationMutationError(
                    new ResourceRegistrationIdentityConflict(resourceTypeId, resourceId)));
        }

        var admission = await AdvanceFenceAsync(
            tenantId,
            resourceGroupId,
            session,
            cancellationToken);
        if (admission is { } admissionError)
            return new Err<ResourceRegistrationMutationError>(admissionError);

        var deletion = await registrations.DeleteOneAsync(
            session,
            ResourceRegistrationFilters.Exact(
                tenantId,
                resourceGroupId,
                resourceTypeId,
                resourceId),
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == 1
            ? new Ok<Unit>(Unit.Value)
            : new Err<ResourceRegistrationMutationError>(
                new ResourceRegistrationMutationError(
                    new ResourceRegistrationIdentityConflict(resourceTypeId, resourceId)));
    }

    private async Task<ResourceRegistrationMutationError?> AdvanceFenceAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var write = await resourceGroups.UpdateOneAsync(
            session,
            ResourceGroupFilters.Active(tenantId, resourceGroupId),
            Builders<ResourceGroup>.Update.Inc(group => group.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        if (write.ModifiedCount == 1)
            return null;

        var exists = await resourceGroups
            .Find(session, ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
            .AnyAsync(cancellationToken);
        return exists
            ? new ResourceRegistrationMutationError(
                new ResourceRegistrationResourceGroupDeleting(resourceGroupId))
            : new ResourceRegistrationMutationError(
                new ResourceRegistrationResourceGroupNotFound(resourceGroupId));
    }

    private static bool IsSameRegistration(
        ResourceRegistration existing,
        ResourceRegistration candidate) =>
        string.Equals(existing.ResourceGroupId, candidate.ResourceGroupId, StringComparison.Ordinal) &&
        string.Equals(existing.DisplayName, candidate.DisplayName, StringComparison.Ordinal) &&
        string.Equals(existing.OwningPluginId, candidate.OwningPluginId, StringComparison.Ordinal);

    private static Err<ResourceRegistrationMutationError> Conflict(
        ResourceRegistrationDescriptor resource) =>
        new(new ResourceRegistrationMutationError(
            new ResourceRegistrationIdentityConflict(
                resource.ResourceTypeId,
                resource.ResourceId)));

    private static RegisteredResource ToContract(ResourceRegistration registration) =>
        new(
            registration.Id,
            registration.TenantId,
            registration.ResourceGroupId,
            ResourceTypeId.Create(registration.ResourceTypeId),
            registration.ResourceId,
            registration.DisplayName,
            registration.OwningPluginId,
            registration.CreatedAtUtc,
            registration.UpdatedAtUtc);

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource registration changes require the resource owner's active MongoDB transaction.");
        }
    }
}
