// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentReferenceUnavailable;

public sealed record ResourceDeploymentReferencesAdmitted;

public union AdmitResourceDeploymentReferencesOutcome(
    ResourceDeploymentReferencesAdmitted,
    ResourceDeploymentReferenceUnavailable);

public delegate Task<AdmitResourceDeploymentReferencesOutcome> AdmitResourceDeploymentReferences(
    AdmitResourceDeploymentReferencesRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record AdmitResourceDeploymentReferencesRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    bool AllDeployments,
    IReadOnlyCollection<string> DeploymentIds
);
