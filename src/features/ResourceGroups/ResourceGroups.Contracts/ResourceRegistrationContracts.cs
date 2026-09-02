using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceRegistrationDescriptor(
    ResourceTypeId ResourceTypeId,
    string ResourceId,
    string DisplayName,
    string? OwningPluginId = null);

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

public delegate Task<Result<RegisteredResource, ResourceRegistrationMutationError>>
    RegisterResource(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<RegisteredResource, ResourceRegistrationMutationError>>
    UpdateResourceRegistration(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, ResourceRegistrationMutationError>>
    UnregisterResource(
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
