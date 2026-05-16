namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.RedisServices.Events
open Pole.Features.Search.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type RedisProjectionConsumer(searchService : ISearchService) =
    member _.HandleUpsert(
        tenantId : string,
        redisId : string,
        name : string,
        resourceGroupId : string,
        apply : SearchDocumentSet -> CancellationToken -> Task,
        cancellationToken : CancellationToken) =
        task {
            let documents = SearchDocumentFactory.fromRedisFields tenantId None redisId name resourceGroupId
            do! apply documents cancellationToken
        }

type RedisCreatedConsumer(searchService : ISearchService) =
    inherit RedisProjectionConsumer(searchService)

    member this.Handle(event : CreateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, searchService.Create, cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RedisUpdatedConsumer(searchService : ISearchService) =
    inherit RedisProjectionConsumer(searchService)

    member this.Handle(event : UpdateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, searchService.Update, cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RedisDeletedConsumer(searchService : ISearchService) =
    member _.Handle(tenantId : string, redisId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.RedisService, tenantId, redisId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.RedisId, context.CancellationToken)
