// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.Domains.Contracts;

public sealed record NamedDomainZoneRequest(
    string TenantId,
    string TechnicalName
);

public sealed record NamedDomainZoneFound(
    string Id
);

public sealed record NamedDomainZoneNotFound(
    string TechnicalName
);

public union NamedDomainZoneOutcome(NamedDomainZoneFound, NamedDomainZoneNotFound);

public delegate Task<NamedDomainZoneOutcome> ResolveNamedDomainZone(
    NamedDomainZoneRequest request,
    IClientSessionHandle? session,
    CancellationToken cancellationToken = default);

public sealed record NamedHostnameBindingRequest(
    string TenantId,
    string TechnicalName
);

public sealed record NamedHostnameBindingFound(
    string Id
);

public sealed record NamedHostnameBindingNotFound(
    string TechnicalName
);

public union NamedHostnameBindingOutcome(NamedHostnameBindingFound, NamedHostnameBindingNotFound);

public delegate Task<NamedHostnameBindingOutcome> ResolveNamedHostnameBinding(
    NamedHostnameBindingRequest request,
    IClientSessionHandle? session,
    CancellationToken cancellationToken = default);

public sealed record LoadDomainZonePlanningRequest(
    string TenantId,
    string Id
);

public sealed record DomainZonePlanningSnapshot(
    TenantDomainZoneSnapshot Zone,
    long ReferenceFence
);

public delegate Task<DomainZonePlanningSnapshot?> LoadDomainZoneForPlanning(
    LoadDomainZonePlanningRequest request,
    IClientSessionHandle? session,
    CancellationToken cancellationToken = default);

public sealed record LoadHostnameBindingPlanningRequest(
    string TenantId,
    string Id
);

public sealed record HostnameBindingPlanningSnapshot(
    HostnameBindingSnapshot Binding,
    long ReferenceFence
);

public delegate Task<HostnameBindingPlanningSnapshot?> LoadHostnameBindingForPlanning(
    LoadHostnameBindingPlanningRequest request,
    IClientSessionHandle? session,
    CancellationToken cancellationToken = default);

public sealed record FenceDomainZoneRequest(
    string TenantId,
    string Id,
    long ExpectedRevision,
    long ExpectedReferenceFence
);

public sealed record DomainZoneFenced;

public sealed record DomainZonePreconditionChanged(
    string Id
);

public union FenceDomainZoneOutcome(DomainZoneFenced, DomainZonePreconditionChanged);

public delegate Task<FenceDomainZoneOutcome> FenceDomainZone(
    FenceDomainZoneRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record FenceHostnameBindingRequest(
    string TenantId,
    string Id,
    long ExpectedReferenceFence
);

public sealed record HostnameBindingFenced;

public sealed record HostnameBindingPreconditionChanged(
    string Id
);

public union FenceHostnameBindingOutcome(HostnameBindingFenced, HostnameBindingPreconditionChanged);

public delegate Task<FenceHostnameBindingOutcome> FenceHostnameBinding(
    FenceHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
