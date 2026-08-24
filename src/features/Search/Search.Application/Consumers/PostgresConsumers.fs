namespace Pole.Features.Search.Application.Consumers

open System.Threading
open MassTransit
open Pole.Features.Postgres.Events
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Persistence

type PostgresSearchProjectionConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    member _.HandleUpsert(
        tenantId : string,
        clusterId : string,
        name : string,
        groupId : string,
        cancellationToken : CancellationToken) =
        task {
            let documents = SearchDocumentFactory.fromPostgresClusterFields tenantId None clusterId name groupId
            do! persistSearchDocumentSet.Invoke(documents, cancellationToken)
        }

type AddPostgresClusterToSearchOnPostgresClusterCreatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit PostgresSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : PostgresClusterCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, cancellationToken)

    interface IConsumer<PostgresClusterCreatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdatePostgresClusterInSearchOnPostgresClusterUpdatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit PostgresSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : PostgresClusterUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, cancellationToken)

    interface IConsumer<PostgresClusterUpdatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemovePostgresClusterFromSearchOnPostgresClusterDeletedConsumer(removeSearchDocumentSet : RemoveSearchDocumentSet) =
    member _.Handle(tenantId : string, clusterId : string, cancellationToken : CancellationToken) =
        removeSearchDocumentSet.Invoke(SearchDocumentType.PostgresCluster, tenantId, clusterId, cancellationToken)

    interface IConsumer<PostgresClusterDeletedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.ClusterId, context.CancellationToken)
