// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Common

open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

type PostgresClusterSpecDto =
    { Instances: int
      Storage: int
      StorageProfileId: string
      Version: int }

type PostgresClusterDto =
    { Id: string
      Name: string
      GroupId: string
      Revision: int64
      Lifecycle: string
      Spec: PostgresClusterSpecDto
      Placement: PlacementSummaryDto
      GitOps: GitResourceStatus }

[<RequireQualifiedAccess>]
module PostgresClusterDto =
    let create
        (cluster: PostgresCluster)
        (placement: PlacementSummaryDto)
        (gitOps: GitResourceStatus)
        : PostgresClusterDto =
        { Id = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Revision = cluster.Revision
          Lifecycle =
            match cluster.Lifecycle with
            | :? ActivePostgresClusterLifecycle when cluster.IsPrepared -> "Prepared"
            | :? ActivePostgresClusterLifecycle -> "Active"
            | :? DeletingPostgresClusterLifecycle -> "Deleting"
            | lifecycle -> invalidOp $"Unsupported PostgreSQL lifecycle '{lifecycle.GetType().Name}'."
          Spec =
            { Instances = cluster.Spec.Instances
              Storage = cluster.Spec.Storage
              StorageProfileId = cluster.Spec.StorageProfileId
              Version = cluster.Spec.Version }
          Placement = placement
          GitOps = gitOps }
