namespace Pole.Features.Search.Application.Consumers

open System.Threading
open MassTransit
open Pole.Features.RedisServices.Events
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Persistence

type RedisServiceSearchProjectionConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    member _.HandleUpsert(
        tenantId : string,
        redisId : string,
        name : string,
        resourceGroupId : string,
        cancellationToken : CancellationToken) =
        task {
            let documents = SearchDocumentFactory.fromRedisFields tenantId None redisId name resourceGroupId
            do! persistSearchDocumentSet.Invoke(documents, cancellationToken)
        }

type AddRedisServiceToSearchOnRedisServiceCreatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit RedisServiceSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : RedisServiceCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, cancellationToken)

    interface IConsumer<RedisServiceCreatedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdateRedisServiceInSearchOnRedisServiceUpdatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit RedisServiceSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : RedisServiceUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(event.EventCore.TenantId, event.RedisId, event.Name, event.ResourceGroupId, cancellationToken)

    interface IConsumer<RedisServiceUpdatedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemoveRedisServiceFromSearchOnRedisServiceDeletedConsumer(removeSearchDocumentSet : RemoveSearchDocumentSet) =
    member _.Handle(tenantId : string, redisId : string, cancellationToken : CancellationToken) =
        removeSearchDocumentSet.Invoke(SearchDocumentType.RedisService, tenantId, redisId, cancellationToken)

    interface IConsumer<RedisServiceDeletedEvent> with
        member this.Consume(context : ConsumeContext<RedisServiceDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.RedisId, context.CancellationToken)
