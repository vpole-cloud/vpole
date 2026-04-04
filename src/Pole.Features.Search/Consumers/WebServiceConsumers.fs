namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open MongoDB.Driver
open Pole.Database
open Pole.Features.Events.WebServices
open Pole.Features.Search.Common
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type WebServiceProjectionConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    member private this.GetResourceGroupName(resourceGroupId : string, cancellationToken : CancellationToken) =
        task {
            let! resourceGroup =
                dbContext.ResourceGroups
                    .Find(fun group -> group.Id = resourceGroupId)
                    .FirstOrDefaultAsync(cancellationToken)

            return if isNull resourceGroup then None else Some resourceGroup.Name
        }

    member this.HandleUpsert(webServiceId : string, apply : SearchDocumentSet -> CancellationToken -> Task, cancellationToken : CancellationToken) =
        task {
            let! webService =
                dbContext.WebServices
                    .Find(fun service -> service.Id = webServiceId)
                    .FirstOrDefaultAsync(cancellationToken)

            if not (isNull webService) then
                let! resourceGroupName = this.GetResourceGroupName(webService.ResourceGroupId, cancellationToken)
                let webDocuments = SearchDocumentFactory.fromWebService resourceGroupName webService
                do! apply webDocuments cancellationToken

                let! dnsRecord =
                    dbContext.DnsRecords
                        .Find(fun record -> record.ResourceId = webServiceId)
                        .FirstOrDefaultAsync(cancellationToken)

                if not (isNull dnsRecord) then
                    let dnsDocuments = SearchDocumentFactory.fromDnsRecord resourceGroupName dnsRecord
                    do! apply dnsDocuments cancellationToken
        }

type WebServiceCreatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit WebServiceProjectionConsumer(dbContext, searchService)

    member this.Handle(webServiceId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(webServiceId, searchService.Create, cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message.WebServiceId, context.CancellationToken)

type WebServiceUpdatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit WebServiceProjectionConsumer(dbContext, searchService)

    member this.Handle(webServiceId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(webServiceId, searchService.Update, cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message.WebServiceId, context.CancellationToken)

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
