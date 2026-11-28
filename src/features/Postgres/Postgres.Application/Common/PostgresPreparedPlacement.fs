// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Common

open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

type PostgresPreparedPlacement =
    { DeploymentId: string
      IsolationScopeId: string
      Target: ResolvedResourceDeploymentTarget
      Compute: ResourceDeploymentComputeConfiguration
      Storage: ResourceDeploymentStorageConfiguration
      StoragePolicyRevision: int64 }

[<RequireQualifiedAccess>]
module PostgresPreparedPlacements =
    let createRequest (cluster: PostgresCluster) (placement: PostgresPreparedPlacement) =
        if
            not cluster.IsPrepared
            || not (cluster.Lifecycle :? ActivePostgresClusterLifecycle)
        then
            invalidArg "cluster" "Deferred placement requires an active prepared PostgreSQL identity."

        if placement.Storage.StorageProfileId <> cluster.Spec.StorageProfileId then
            invalidArg "placement" "The prepared storage profile must match the PostgreSQL configuration."

        if placement.Compute.DeploymentTargetId <> placement.Target.DeploymentTargetId then
            invalidArg "placement" "The prepared compute profile must match the PostgreSQL target."

        CreateResourceDeploymentRequest(
            placement.DeploymentId,
            cluster.TenantId,
            PostgresResourceDeployment.ResourceType,
            cluster.Id,
            cluster.GroupId,
            placement.IsolationScopeId,
            placement.Target,
            placement.Compute,
            placement.Storage,
            ResourcePlacementCapability.SingleTarget,
            FixedReplicas = cluster.Spec.Instances,
            ResourceRevision = cluster.Revision,
            DeferredActivation = true,
            PinnedCompute = true,
            StoragePolicyRevision = System.Nullable placement.StoragePolicyRevision
        )
