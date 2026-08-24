namespace Pole.Features.Search.Application.Consumers

open System.Threading
open MassTransit
open Pole.Features.WebServices.Events
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Persistence

type WebServiceSearchProjectionConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    member _.HandleUpsert(
        tenantId : string,
        webServiceId : string,
        name : string,
        resourceGroupId : string,
        hostnames : seq<string>,
        image : string,
        cancellationToken : CancellationToken) =
        let documents =
            SearchDocumentFactory.fromWebServiceFields
                tenantId
                None
                webServiceId
                name
                resourceGroupId
                hostnames
                image
        persistSearchDocumentSet.Invoke(documents, cancellationToken)

type AddWebServiceToSearchOnWebServiceCreatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit WebServiceSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : WebServiceCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.EventCore.TenantId,
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.Deployments |> Seq.collect _.Hostnames,
            event.Spec.Image.FullImageName,
            cancellationToken)

    interface IConsumer<WebServiceCreatedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdateWebServiceInSearchOnWebServiceUpdatedConsumer(persistSearchDocumentSet : PersistSearchDocumentSet) =
    inherit WebServiceSearchProjectionConsumer(persistSearchDocumentSet)

    member this.Handle(event : WebServiceUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.EventCore.TenantId,
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.Deployments |> Seq.collect _.Hostnames,
            event.Spec.Image.FullImageName,
            cancellationToken)

    interface IConsumer<WebServiceUpdatedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemoveWebServiceFromSearchOnWebServiceDeletedConsumer(removeSearchDocumentSet : RemoveSearchDocumentSet) =
    member _.Handle(tenantId : string, webServiceId : string, cancellationToken : CancellationToken) =
        removeSearchDocumentSet.Invoke(SearchDocumentType.WebService, tenantId, webServiceId, cancellationToken)

    interface IConsumer<WebServiceDeletedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.WebServiceId, context.CancellationToken)
