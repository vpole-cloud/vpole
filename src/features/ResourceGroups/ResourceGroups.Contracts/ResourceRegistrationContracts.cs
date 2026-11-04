using MongoDB.Driver;
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

public union ResourceRegistrationOutcome(
    RegisteredResource,
    ResourceRegistrationMutationError);

public sealed record ResourceUnregistered;

public union ResourceUnregistrationOutcome(
    ResourceUnregistered,
    ResourceRegistrationMutationError);

public interface IResourceRegistrar
{
    Task<ResourceRegistrationOutcome> RegisterAsync(
        RegisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<ResourceRegistrationOutcome> UpdateAsync(
        UpdateRegisteredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<ResourceUnregistrationOutcome> UnregisterAsync(
        UnregisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

// Acquires a write on the tenant's resource registration in the caller's Mongo
// transaction, so a connection can resolve placements against a stable owner.
public delegate Task<RegisteredResource?> FenceRegisteredResource(
    FenceRegisteredResourceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

/// <summary>Tenant-scoped advisory identity read. Does not acquire a mutation fence.</summary>
public delegate Task<RegisteredResource?> LoadRegisteredResource(
    LoadRegisteredResourceRequest request,
    CancellationToken cancellationToken = default);

public sealed record RegisterResourceRequest(
    string TenantId,
    string ResourceGroupId,
    ResourceRegistrationDescriptor Resource);

public sealed record UpdateRegisteredResourceRequest(
    string TenantId,
    string ResourceGroupId,
    ResourceRegistrationDescriptor Resource);

public sealed record UnregisterResourceRequest(
    string TenantId,
    string ResourceGroupId,
    ResourceTypeId ResourceTypeId,
    string ResourceId);

public sealed record FenceRegisteredResourceRequest(
    string TenantId,
    ResourceTypeId ResourceTypeId,
    string ResourceId);

public sealed record LoadRegisteredResourceRequest(
    string TenantId,
    ResourceTypeId ResourceTypeId,
    string ResourceId);

public sealed record ResourceRegistrationPlanningSnapshot(
    RegisteredResource Resource,
    long ReferenceFence);

public sealed record ResourceRegistrationSnapshotFenced;

public sealed record ResourceRegistrationSnapshotChanged;

public union FenceResourceRegistrationSnapshotOutcome(
    ResourceRegistrationSnapshotFenced,
    ResourceRegistrationSnapshotChanged);

public delegate Task<ResourceRegistrationPlanningSnapshot?> LoadResourceRegistrationPlanningSnapshot(
    LoadRegisteredResourceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public delegate Task<FenceResourceRegistrationSnapshotOutcome> FenceResourceRegistrationPlanningSnapshot(
    ResourceRegistrationPlanningSnapshot snapshot,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
