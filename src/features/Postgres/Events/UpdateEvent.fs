namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Postgres.Domain
open Pole.Features.Events.Abstractions
open Pole.Observability

type UpdateEventChanges =
    { Spec : UpdateLog<PostgresClusterSpec> }

[<EntityName("update-postgres-cluster-requested")>]
type UpdateEvent =
    { ClusterId : string
      Name : string
      GroupId : string
      Changes : UpdateEventChanges
      TraceId : string
      CorrelationId : string }
    interface IPoleEvent with
        member this.TraceId = this.TraceId
        member this.CorrelationId = this.CorrelationId

module UpdateEvent =
    let create (previous : PostgresCluster) (next : PostgresCluster) : UpdateEvent =
        { ClusterId = next.Id
          Name = next.Name
          GroupId = next.GroupId
          Changes =
            { Spec = UpdateLog<PostgresClusterSpec>.Of(previous.Spec, next.Spec) }
          TraceId = TraceContext.CurrentTraceId
          CorrelationId = TraceContext.CurrentCorrelationId }

    let toPostgresCluster (event : UpdateEvent) : PostgresCluster =
        { Id = event.ClusterId
          Name = event.Name
          GroupId = event.GroupId
          Spec = event.Changes.Spec.New }
