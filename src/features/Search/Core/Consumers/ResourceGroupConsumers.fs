namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open MongoDB.Driver
open Pole.Database
open Pole.Features.ResourceGroups.Events
open Pole.Features.Search.Common
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type ResourceGroupCreatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    member _.Handle(resourceGroupId : string, cancellationToken : CancellationToken) =
        task {
            let! resourceGroup =
                dbContext.ResourceGroups
                    .Find(fun group -> group.Id = resourceGroupId)
                    .FirstOrDefaultAsync(cancellationToken)

            if not (isNull resourceGroup) then
                let documents = SearchDocumentFactory.fromResourceGroup resourceGroup
                do! searchService.Create documents cancellationToken
        }

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message.ResourceGroupId, context.CancellationToken)

type ResourceGroupUpdatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    member _.Handle(resourceGroupId : string, cancellationToken : CancellationToken) =
        task {
            let! resourceGroup =
                dbContext.ResourceGroups
                    .Find(fun group -> group.Id = resourceGroupId)
                    .FirstOrDefaultAsync(cancellationToken)

            if not (isNull resourceGroup) then
                let documents = SearchDocumentFactory.fromResourceGroup resourceGroup
                do! searchService.Update documents cancellationToken
        }

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message.ResourceGroupId, context.CancellationToken)

type ResourceGroupDeletedConsumer(searchService : ISearchService) =
    member _.Handle(resourceGroupId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.ResourceGroup, resourceGroupId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.ResourceGroupId, context.CancellationToken)
