namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.Postgres.Events
open Pole.Features.Search.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type PostgresProjectionConsumer(searchService : ISearchService) =
    member _.HandleUpsert(
        tenantId : string,
        clusterId : string,
        name : string,
        groupId : string,
        apply : SearchDocumentSet -> CancellationToken -> Task,
        cancellationToken : CancellationToken) =
        task {
            let documents = SearchDocumentFactory.fromPostgresClusterFields tenantId None clusterId name groupId
            do! apply documents cancellationToken
        }

type PostgresCreatedConsumer(searchService : ISearchService) =
    inherit PostgresProjectionConsumer(searchService)

    member this.Handle(event : CreateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, searchService.Create, cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type PostgresUpdatedConsumer(searchService : ISearchService) =
    inherit PostgresProjectionConsumer(searchService)

    member this.Handle(event : UpdateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, searchService.Update, cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type PostgresDeletedConsumer(searchService : ISearchService) =
    member _.Handle(tenantId : string, clusterId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.PostgresCluster, tenantId, clusterId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.ClusterId, context.CancellationToken)
