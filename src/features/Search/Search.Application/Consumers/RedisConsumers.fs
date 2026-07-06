namespace Pole.Features.Search.Application.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.RedisServices.Events
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Services

type RedisServiceSearchProjectionConsumer(searchService : ISearchService) =
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

type AddRedisServiceToSearchOnRedisServiceCreatedConsumer(searchService : ISearchService) =
    inherit RedisServiceSearchProjectionConsumer(searchService)

    member this.Handle(event : RedisServiceCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, searchService.Create, cancellationToken)

    interface IConsumer<RedisServiceCreatedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdateRedisServiceInSearchOnRedisServiceUpdatedConsumer(searchService : ISearchService) =
    inherit RedisServiceSearchProjectionConsumer(searchService)

    member this.Handle(event : RedisServiceUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, searchService.Update, cancellationToken)

    interface IConsumer<RedisServiceUpdatedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemoveRedisServiceFromSearchOnRedisServiceDeletedConsumer(searchService : ISearchService) =
    member _.Handle(tenantId : string, redisId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.RedisService, tenantId, redisId) cancellationToken

    interface IConsumer<RedisServiceDeletedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.RedisId, context.CancellationToken)

