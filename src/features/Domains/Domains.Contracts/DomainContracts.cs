// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Domains.Contracts;

public static class DomainZoneKinds
{
    public const string Generated = "generated";
    public const string ManagedReservation = "managed-reservation";
}

public static class HostnameBindingKinds
{
    public const string Generated = "generated";
    public const string Custom = "custom";
}

public static class DomainCapabilityLimits
{
    public const int MaximumSetSize = 64;
}

public sealed record TenantDomainZoneSnapshot(
    string Id,
    string TenantId,
    string DeploymentTargetId,
    string Label,
    string Domain,
    string Kind,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? TechnicalName
);

public sealed record HostnameBindingSnapshot(
    string Id,
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName,
    string Hostname,
    string Kind,
    DateTime CreatedAtUtc,
    string? TechnicalName
);

public sealed record GetOrCreateGeneratedZoneRequest(
    string TenantId,
    string DeploymentTargetId,
    string PublicBaseDomain,
    long ExpectedWorkloadConfigurationRevision
);

public sealed record CreateGeneratedHostnameBindingRequest(
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName
);

public sealed record CreateCustomHostnameBindingRequest(
    string BindingId,
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName,
    string TechnicalName
);

public sealed record DeleteCustomHostnameBindingRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string BindingId
);

public sealed record CustomHostnameBindingZoneNotFound(
    string TenantDomainZoneId
);

public sealed record CustomHostnameBindingTargetMismatch(
    string TenantDomainZoneId,
    string DeploymentTargetId
);

public sealed record CustomHostnameBindingConflict(
    string Hostname
);

public sealed record CustomHostnameBindingInvalidRelativeName;

public sealed record CustomHostnameBindingNameConflict(
    string TechnicalName
);

public sealed record CustomHostnameBindingNotFound(
    string BindingId
);

public sealed record GeneratedHostnameBindingDeletionRejected(
    string BindingId
);

public sealed record CustomHostnameBindingZoneUnavailable(
    string TenantDomainZoneId
);

public sealed record CustomHostnameBindingZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds
);

public sealed record CustomHostnameBindingDeletionConflict(
    string BindingId
);

public union CustomHostnameBindingError(
    CustomHostnameBindingInvalidRelativeName,
    CustomHostnameBindingZoneNotFound,
    CustomHostnameBindingTargetMismatch,
    CustomHostnameBindingConflict,
    CustomHostnameBindingNameConflict,
    CustomHostnameBindingNotFound,
    GeneratedHostnameBindingDeletionRejected,
    CustomHostnameBindingZoneUnavailable,
    CustomHostnameBindingZoneChanged,
    CustomHostnameBindingDeletionConflict);

public sealed record GeneratedDomainZoneTenantUnavailable(
    string TenantId
);

public sealed record GeneratedDomainZoneTargetChanged(
    IReadOnlyList<string> DeploymentTargetIds
);

public sealed record GeneratedDomainZoneMismatch(
    string DeploymentTargetId
);

public sealed record GeneratedDomainZoneConflict(
    IReadOnlyList<string> DeploymentTargetIds
);

public union GeneratedDomainZoneError(
    GeneratedDomainZoneTenantUnavailable,
    GeneratedDomainZoneTargetChanged,
    GeneratedDomainZoneMismatch,
    GeneratedDomainZoneConflict);

public sealed record GeneratedHostnameBindingZoneNotFound(
    string TenantDomainZoneId
);

public sealed record GeneratedHostnameBindingTargetMismatch(
    string TenantDomainZoneId,
    string DeploymentTargetId
);

public sealed record GeneratedHostnameBindingZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds
);

public sealed record GeneratedHostnameBindingConflict(
    IReadOnlyList<string> Hostnames
);

public union GeneratedHostnameBindingError(
    GeneratedHostnameBindingZoneNotFound,
    GeneratedHostnameBindingTargetMismatch,
    GeneratedHostnameBindingZoneChanged,
    GeneratedHostnameBindingConflict);

/// <summary>
///     Expected provider states while removing generated or custom hostname bindings as part of a
///     resource-deployment finalization. Consumers use this contract inside their own transaction;
///     it deliberately has no HTTP meaning.
/// </summary>
public sealed record HostnameBindingCleanupZoneUnavailable(
    IReadOnlyList<string> TenantDomainZoneIds
);

public sealed record HostnameBindingCleanupZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds
);

public sealed record HostnameBindingCleanupBindingsChanged;

public sealed record HostnameBindingCleanupReferenceRemovalRejected(
    string TenantDomainZoneId,
    string DeploymentTargetId
);

public union HostnameBindingCleanupError(
    HostnameBindingCleanupZoneUnavailable,
    HostnameBindingCleanupZoneChanged,
    HostnameBindingCleanupBindingsChanged,
    HostnameBindingCleanupReferenceRemovalRejected);

public delegate Task<GetOrCreateGeneratedDomainZoneOutcome> GetOrCreateGeneratedDomainZone(
    GetOrCreateGeneratedZoneRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<GetOrCreateGeneratedDomainZonesOutcome> GetOrCreateGeneratedDomainZones(
    IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<HostnameBindingSnapshot>> LoadHostnameBindingsForDeployment(
    LoadHostnameBindingsForDeploymentRequest request,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<HostnameBindingSnapshot>> LoadHostnameBindingsForDeploymentInTransaction(
    LoadHostnameBindingsForDeploymentInTransactionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CreateGeneratedHostnameBindingOutcome> CreateGeneratedHostnameBinding(
    CreateGeneratedHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CreateGeneratedHostnameBindingsOutcome> CreateGeneratedHostnameBindings(
    IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CreateCustomHostnameBindingOutcome> CreateCustomHostnameBinding(
    CreateCustomHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<DeleteCustomHostnameBindingOutcome> DeleteCustomHostnameBinding(
    DeleteCustomHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CleanupHostnameBindingsOutcome> DeleteHostnameBindingsForDeployment(
    DeleteHostnameBindingsForDeploymentRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CleanupHostnameBindingsOutcome> DeleteHostnameBindingsForResource(
    DeleteHostnameBindingsForResourceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record CustomHostnameBindingDeleted;

public sealed record CustomHostnameBindingGitOwned(
    string BindingId
);

public sealed record HostnameBindingsCleanedUp;

public union GetOrCreateGeneratedDomainZoneOutcome(TenantDomainZoneSnapshot, GeneratedDomainZoneError);

public union GetOrCreateGeneratedDomainZonesOutcome(IReadOnlyList<TenantDomainZoneSnapshot>, GeneratedDomainZoneError);

public union CreateGeneratedHostnameBindingOutcome(HostnameBindingSnapshot, GeneratedHostnameBindingError);

public union CreateGeneratedHostnameBindingsOutcome(
    IReadOnlyList<HostnameBindingSnapshot>,
    GeneratedHostnameBindingError);

public union CreateCustomHostnameBindingOutcome(HostnameBindingSnapshot, CustomHostnameBindingError);

public union DeleteCustomHostnameBindingOutcome(
    CustomHostnameBindingDeleted,
    CustomHostnameBindingGitOwned,
    CustomHostnameBindingError);

public union CleanupHostnameBindingsOutcome(HostnameBindingsCleanedUp, HostnameBindingCleanupError);

public sealed record LoadHostnameBindingsForDeploymentRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId
);

public sealed record LoadHostnameBindingsForDeploymentInTransactionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId
);

public sealed record DeleteHostnameBindingsForDeploymentRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId
);

public sealed record DeleteHostnameBindingsForResourceRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId
);
