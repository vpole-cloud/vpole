namespace Pole.Features.Search.Application.Consumers

open System.Threading
open MassTransit
open Pole.Features.ResourceGroups.Events
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Persistence

type AddResourceGroupToSearchOnResourceGroupCreatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    member _.Handle(event : ResourceGroupCreatedEvent, cancellationToken : CancellationToken) =
        let documents = SearchDocumentFactory.fromResourceGroupFields event.EventCore.TenantId event.ResourceGroupId event.Name event.Tags
        persistSearchDocumentSet.Invoke(documents, cancellationToken)

    interface IConsumer<ResourceGroupCreatedEvent> with
        member this.Consume(context : ConsumeContext<ResourceGroupCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdateResourceGroupInSearchOnResourceGroupUpdatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    member _.Handle(event : ResourceGroupUpdatedEvent, cancellationToken : CancellationToken) =
        let documents = SearchDocumentFactory.fromResourceGroupFields event.EventCore.TenantId event.ResourceGroupId event.Name event.Changes.Tags.New
        persistSearchDocumentSet.Invoke(documents, cancellationToken)

    interface IConsumer<ResourceGroupUpdatedEvent> with
        member this.Consume(context : ConsumeContext<ResourceGroupUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemoveResourceGroupFromSearchOnResourceGroupDeletedConsumer(removeSearchDocumentSet : RemoveSearchDocumentSet) =
    member _.Handle(tenantId : string, resourceGroupId : string, cancellationToken : CancellationToken) =
        removeSearchDocumentSet.Invoke(SearchDocumentType.ResourceGroup, tenantId, resourceGroupId, cancellationToken)

    interface IConsumer<ResourceGroupDeletedEvent> with
        member this.Consume(context : ConsumeContext<ResourceGroupDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.ResourceGroupId, context.CancellationToken)
