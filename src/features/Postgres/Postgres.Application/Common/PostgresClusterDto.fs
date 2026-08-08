namespace Pole.Features.Postgres.Application.Common

open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

type PostgresClusterSpecDto =
    { Instances : int
      Storage : int
      StorageProfileId : string
      Version : int
      DeploymentMode : string }

type PostgresClusterDto =
    { Id : string
      Name : string
      GroupId : string
      Revision : int64
      Lifecycle : string
      Spec : PostgresClusterSpecDto
      Placement : PlacementSummaryDto }

[<RequireQualifiedAccess>]
module PostgresClusterDto =
    let create (cluster : PostgresCluster) (placement : PlacementSummaryDto) : PostgresClusterDto =
        { Id = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Revision = cluster.Revision
          Lifecycle =
            match cluster.Lifecycle with
            | :? ActivePostgresClusterLifecycle -> "Active"
            | :? DeletingPostgresClusterLifecycle -> "Deleting"
            | lifecycle -> invalidOp $"Unsupported PostgreSQL lifecycle '{lifecycle.GetType().Name}'."
          Spec =
            { Instances = cluster.Spec.Instances
              Storage = cluster.Spec.Storage
              StorageProfileId = cluster.Spec.StorageProfileId
              Version = cluster.Spec.Version
              DeploymentMode = cluster.Spec.DeploymentMode.ToString() }
          Placement = placement }
