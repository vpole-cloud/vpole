namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

type PostgresDeploymentModeSnapshot =
    | Operator = 0
    | StatefulSet = 1

type PostgresClusterSpecSnapshot =
    { Instances : int
      Storage : int
      KubernetesNamespace : string
      Version : int
      DeploymentMode : PostgresDeploymentModeSnapshot }

[<EntityName("postgres-cluster-created")>]
type PostgresClusterCreatedEvent =
    { EventCore : EventCore
      ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpecSnapshot }
    interface IPoleEvent with
        member this.EventCore = this.EventCore
