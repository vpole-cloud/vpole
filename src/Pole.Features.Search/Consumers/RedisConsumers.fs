namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open MongoDB.Driver
open Pole.Database
open Pole.Features.Events.RedisServices
open Pole.Features.Search.Common
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type RedisProjectionConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    member private this.GetResourceGroupName(resourceGroupId : string, cancellationToken : CancellationToken) =
        task {
            let! resourceGroup =
                dbContext.ResourceGroups
                    .Find(fun group -> group.Id = resourceGroupId)
                    .FirstOrDefaultAsync(cancellationToken)

            return if isNull resourceGroup then None else Some resourceGroup.Name
        }

    member this.HandleUpsert(redisId : string, apply : SearchDocumentSet -> CancellationToken -> Task, cancellationToken : CancellationToken) =
        task {
            let! redis =
                dbContext.RedisServices
                    .Find(fun service -> service.Id = redisId)
                    .FirstOrDefaultAsync(cancellationToken)

            if not (isNull redis) then
                let! resourceGroupName = this.GetResourceGroupName(redis.ResourceGroupId, cancellationToken)
                let documents = SearchDocumentFactory.fromRedis resourceGroupName redis
                do! apply documents cancellationToken
        }

type RedisCreatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit RedisProjectionConsumer(dbContext, searchService)

    member this.Handle(redisId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(redisId, searchService.Create, cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message.RedisId, context.CancellationToken)

type RedisUpdatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit RedisProjectionConsumer(dbContext, searchService)

    member this.Handle(redisId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(redisId, searchService.Update, cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message.RedisId, context.CancellationToken)

type RedisDeletedConsumer(searchService : ISearchService) =
    member _.Handle(redisId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.RedisService, redisId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.RedisId, context.CancellationToken)
