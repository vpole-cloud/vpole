namespace Pole.Features.Search.Application.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.WebServices.Events
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Application.Projection
open Pole.Features.Search.Application.Services

type WebServiceSearchProjectionConsumer(searchService : ISearchService) =
    member _.HandleUpsert(
        tenantId : string,
        webServiceId : string,
        name : string,
        resourceGroupId : string,
        hostnames : seq<string>,
        image : string,
        apply : SearchDocumentSet -> CancellationToken -> Task,
        cancellationToken : CancellationToken) =
        let resourceGroupName = None
        let webDocuments =
            SearchDocumentFactory.fromWebServiceFields tenantId resourceGroupName webServiceId name resourceGroupId hostnames image
        apply webDocuments cancellationToken

type AddWebServiceToSearchOnWebServiceCreatedConsumer(searchService : ISearchService) =
    inherit WebServiceSearchProjectionConsumer(searchService)

    member this.Handle(event : WebServiceCreatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.EventCore.TenantId,
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.Deployments |> Seq.collect _.Hostnames,
            event.Spec.Image.FullImageName,
            searchService.Create,
            cancellationToken)

    interface IConsumer<WebServiceCreatedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdateWebServiceInSearchOnWebServiceUpdatedConsumer(searchService : ISearchService) =
    inherit WebServiceSearchProjectionConsumer(searchService)

    member this.Handle(event : WebServiceUpdatedEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.EventCore.TenantId,
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.Deployments |> Seq.collect _.Hostnames,
            event.Spec.Image.FullImageName,
            searchService.Update,
            cancellationToken)

    interface IConsumer<WebServiceUpdatedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemoveWebServiceFromSearchOnWebServiceDeletedConsumer(searchService : ISearchService) =
    member _.Handle(tenantId : string, webServiceId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.WebService, tenantId, webServiceId) cancellationToken

    interface IConsumer<WebServiceDeletedEvent> with
        member this.Consume(context : ConsumeContext<WebServiceDeletedEvent>) =
            this.Handle(context.Message.EventCore.TenantId, context.Message.WebServiceId, context.CancellationToken)
