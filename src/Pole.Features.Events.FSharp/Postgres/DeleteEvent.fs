namespace Pole.Features.Events.FSharp.Postgres

open MassTransit
open Pole.Features.Events.Abstractions
open Pole.Domain.Models.FSharp

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { ClusterId : string
      NamespaceName : string
      DeploymentMode : PostgresDeploymentMode }
    interface IPoleEvent
    
module DeleteEvent =
    let create clusterId namespaceName deploymentMode : DeleteEvent =
        { ClusterId = clusterId
          NamespaceName = namespaceName
          DeploymentMode = deploymentMode }
