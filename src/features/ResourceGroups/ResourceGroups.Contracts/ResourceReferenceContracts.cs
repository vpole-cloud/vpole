// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceScopeConsumerReference(
    ResourceTypeId ResourceType,
    string ResourceId
);

// Admission takes the group write fence shared by deletion and unregistration.
public sealed record ResourceScopeReferenceUnavailable;

public sealed record ResourceScopeReferenceAdmitted;

public union AdmitResourceScopeReferenceOutcome(ResourceScopeReferenceAdmitted, ResourceScopeReferenceUnavailable);

public delegate Task<AdmitResourceScopeReferenceOutcome> AdmitResourceScopeReference(
    AdmitResourceScopeReferenceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record AdmitResourceScopeReferenceRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    ResourceScopeConsumerReference? Consumer
);
