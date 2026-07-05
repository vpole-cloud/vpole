namespace Pole.Features.Search.Application.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.Postgres.Events
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Services

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

    member this.Handle(event : PostgresClusterCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, searchService.Create, cancellationToken)

    interface IConsumer<PostgresClusterCreatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type PostgresUpdatedConsumer(searchService : ISearchService) =
    inherit PostgresProjectionConsumer(searchService)

    member this.Handle(event : PostgresClusterUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.ClusterId, event.Name, event.GroupId, searchService.Update, cancellationToken)

    interface IConsumer<PostgresClusterUpdatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type PostgresDeletedConsumer(searchService : ISearchService) =
    member _.Handle(tenantId : string, clusterId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.PostgresCluster, tenantId, clusterId) cancellationToken

    interface IConsumer<PostgresClusterDeletedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.ClusterId, context.CancellationToken)

