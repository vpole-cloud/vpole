namespace Pole.Features.Search.Consumers

open System.Threading
open MassTransit
open Pole.Features.ResourceGroups.Events
open Pole.Features.Search.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type ResourceGroupCreatedConsumer(searchService : ISearchService) =
    member _.Handle(event : CreateEvent, cancellationToken : CancellationToken) =
        let documents = SearchDocumentFactory.fromResourceGroupFields event.ResourceGroupId event.Name event.Tags
        searchService.Create documents cancellationToken

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type ResourceGroupUpdatedConsumer(searchService : ISearchService) =
    member _.Handle(event : UpdateEvent, cancellationToken : CancellationToken) =
        let documents = SearchDocumentFactory.fromResourceGroupFields event.ResourceGroupId event.Name event.Changes.Tags.New
        searchService.Update documents cancellationToken

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type ResourceGroupDeletedConsumer(searchService : ISearchService) =
    member _.Handle(resourceGroupId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.ResourceGroup, resourceGroupId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.ResourceGroupId, context.CancellationToken)
