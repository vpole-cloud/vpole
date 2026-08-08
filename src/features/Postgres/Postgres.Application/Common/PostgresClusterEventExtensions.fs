namespace Pole.Features.Postgres.Application.Common

open Pole.Features.Events.Abstractions
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Events

[<RequireQualifiedAccess>]
module PostgresClusterEvents =
    let private toSnapshotDeploymentMode mode =
        match mode with
        | PostgresDeploymentMode.Operator -> PostgresDeploymentModeSnapshot.Operator
        | PostgresDeploymentMode.StatefulSet -> PostgresDeploymentModeSnapshot.StatefulSet
        | _ -> invalidArg (nameof mode) $"Unsupported PostgreSQL deployment mode '{mode}'."

    let private toSnapshotSpec (spec : PostgresClusterSpec) : PostgresClusterSpecSnapshot =
        { Instances = spec.Instances
          Storage = spec.Storage
          StorageProfileId = spec.StorageProfileId
          Version = spec.Version
          DeploymentMode = toSnapshotDeploymentMode spec.DeploymentMode }

    let created (cluster : PostgresCluster) : PostgresClusterCreatedEvent =
        { EventCore = EventCore.Create(cluster.TenantId)
          ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = toSnapshotSpec cluster.Spec }

    let deleted (cluster : PostgresCluster) : PostgresClusterDeletedEvent =
        { EventCore = EventCore.Create(cluster.TenantId)
          ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = toSnapshotSpec cluster.Spec }

    let updated (previous : PostgresCluster) (next : PostgresCluster) : PostgresClusterUpdatedEvent =
        { EventCore = EventCore.Create(next.TenantId)
          ClusterId = next.Id
          Name = next.Name
          GroupId = next.GroupId
          Changes =
            { Spec =
                UpdateLog<PostgresClusterSpecSnapshot>.Of(
                    toSnapshotSpec previous.Spec,
                    toSnapshotSpec next.Spec) } }
