namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions
open Pole.Observability

[<EntityName("delete-postgres-cluster-requested")>]
type DeleteEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec
      TraceId : string
      CorrelationId : string }
    interface IPoleEvent with
        member this.TraceId = this.TraceId
        member this.CorrelationId = this.CorrelationId

module DeleteEvent =
    let create (cluster : PostgresCluster) : DeleteEvent =
        { ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec
          TraceId = TraceContext.CurrentTraceId
          CorrelationId = TraceContext.CurrentCorrelationId }
