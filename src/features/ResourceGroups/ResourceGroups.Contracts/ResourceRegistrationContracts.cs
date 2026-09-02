using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceRegistrationDescriptor(
    ResourceTypeId ResourceTypeId,
    string ResourceId,
    string DisplayName);

public sealed record RegisteredResource(
    string RegistrationId,
    string TenantId,
    string ResourceGroupId,
    ResourceTypeId ResourceTypeId,
    string ResourceId,
    string DisplayName,
    string? OwningPluginId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ResourceRegistrationResourceGroupNotFound(string ResourceGroupId);

public sealed record ResourceRegistrationResourceGroupDeleting(string ResourceGroupId);

public sealed record ResourceRegistrationIdentityConflict(
    ResourceTypeId ResourceTypeId,
    string ResourceId);

public sealed record ResourceRegistrationNotFound(
    ResourceTypeId ResourceTypeId,
    string ResourceId);

public union ResourceRegistrationMutationError(
    ResourceRegistrationResourceGroupNotFound,
    ResourceRegistrationResourceGroupDeleting,
    ResourceRegistrationIdentityConflict,
    ResourceRegistrationNotFound);

public interface IResourceRegistrar
{
    Task<Result<RegisteredResource, ResourceRegistrationMutationError>> RegisterAsync(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<RegisteredResource, ResourceRegistrationMutationError>> UpdateAsync(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<Unit, ResourceRegistrationMutationError>> UnregisterAsync(
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
