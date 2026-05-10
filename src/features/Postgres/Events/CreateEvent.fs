namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions
open Pole.Observability

[<EntityName("create-postgres-cluster-requested")>]
type CreateEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Spec : PostgresClusterSpec
      TraceId : string
      CorrelationId : string }
    interface IPoleEvent with
        member this.TraceId = this.TraceId
        member this.CorrelationId = this.CorrelationId

module CreateEvent =
    let create (cluster : PostgresCluster) : CreateEvent =
        { ClusterId = cluster.Id
          Name = cluster.Name
          GroupId = cluster.GroupId
          Spec = cluster.Spec
          TraceId = TraceContext.CurrentTraceId
          CorrelationId = TraceContext.CurrentCorrelationId }

    let toPostgresCluster (event : CreateEvent) : PostgresCluster =
        { Id = event.ClusterId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Spec }
