// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.DeploymentTargets.Contracts;

public sealed record NamedDeploymentTargetRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string Name
);

public sealed record NamedDeploymentTargetUnavailable(
    string Name
);

public sealed record NamedDeploymentTargetAmbiguous(
    string Name
);

public union ResolveNamedDeploymentTargetOutcome(
    DeploymentTargetLookupDto,
    NamedDeploymentTargetUnavailable,
    NamedDeploymentTargetAmbiguous);

public delegate Task<ResolveNamedDeploymentTargetOutcome> ResolveNamedDeploymentTarget(
    NamedDeploymentTargetRequest request,
    CancellationToken cancellationToken = default);
