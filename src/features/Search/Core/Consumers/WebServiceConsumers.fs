namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.WebServices.Events
open Pole.Features.Search.Common
open Pole.Features.Search.Domain
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type WebServiceProjectionConsumer(searchService : ISearchService) =
    member _.HandleUpsert(
        webServiceId : string,
        name : string,
        resourceGroupId : string,
        dnsRecordId : string,
        domain : string,
        image : string,
        apply : SearchDocumentSet -> CancellationToken -> Task,
        cancellationToken : CancellationToken) =
        task {
            let resourceGroupName = None
            let webDocuments =
                SearchDocumentFactory.fromWebServiceFields resourceGroupName webServiceId name resourceGroupId domain image
            do! apply webDocuments cancellationToken

            if not (System.String.IsNullOrWhiteSpace(dnsRecordId)) then
                let dnsDocuments =
                    SearchDocumentFactory.fromDnsRecordFields resourceGroupName dnsRecordId domain resourceGroupId name
                do! apply dnsDocuments cancellationToken
        }

type WebServiceCreatedConsumer(searchService : ISearchService) =
    inherit WebServiceProjectionConsumer(searchService)

    member this.Handle(event : CreateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.DnsRecord.Id,
            event.DnsRecord.Domain,
            event.Spec.Image.FullImageName,
            searchService.Create,
            cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type WebServiceUpdatedConsumer(searchService : ISearchService) =
    inherit WebServiceProjectionConsumer(searchService)

    member this.Handle(event : UpdateEvent, cancellationToken : CancellationToken) =
        this.HandleUpsert(
            event.WebServiceId,
            event.Name,
            event.ResourceGroupId,
            event.DnsRecord.Id,
            event.DnsRecord.Domain,
            event.Changes.WebServiceSpec.New.Image.FullImageName,
            searchService.Update,
            cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type WebServiceDeletedConsumer(searchService : ISearchService) =
    member _.Handle(webServiceId : string, dnsRecordId : string option, cancellationToken : CancellationToken) =
        task {
            do! searchService.Delete(SearchDocumentType.WebService, webServiceId) cancellationToken

            match dnsRecordId with
            | Some value when not (System.String.IsNullOrWhiteSpace(value)) ->
                do! searchService.Delete(SearchDocumentType.DnsRecord, value) cancellationToken
            | _ -> ()
        }

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.WebServiceId, Option.ofObj context.Message.DnsRecordId, context.CancellationToken)
