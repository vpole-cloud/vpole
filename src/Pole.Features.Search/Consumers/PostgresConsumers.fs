namespace Pole.Features.Search.Consumers

open System.Threading
open System.Threading.Tasks
open MassTransit
open MongoDB.Driver
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Postgres.Common
open Pole.Features.Events.FSharp.Postgres
open Pole.Features.Search.Common
open Pole.Features.Search.Projection
open Pole.Features.Search.Services

type PostgresProjectionConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    member private this.GetResourceGroupName(groupId : string, cancellationToken : CancellationToken) =
        task {
            let! resourceGroup =
                dbContext.ResourceGroups
                    .Find(fun group -> group.Id = groupId)
                    .FirstOrDefaultAsync(cancellationToken)

            return if isNull resourceGroup then None else Some resourceGroup.Name
        }

    member this.HandleUpsert(clusterId : string, apply : SearchDocumentSet -> CancellationToken -> Task, cancellationToken : CancellationToken) =
        task {
            let! cluster =
                dbContext.Postgres
                    .Find(Builders<PostgresCluster>.Filter.Eq("Id", clusterId))
                    .FirstOrDefaultAsyncF(cancellationToken)

            match cluster with
            | None -> ()
            | Some cluster ->
                let! resourceGroupName = this.GetResourceGroupName(cluster.GroupId, cancellationToken)
                let documents = SearchDocumentFactory.fromPostgresCluster resourceGroupName cluster
                do! apply documents cancellationToken
        }

type PostgresCreatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit PostgresProjectionConsumer(dbContext, searchService)

    member this.Handle(clusterId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(clusterId, searchService.Create, cancellationToken)

    interface IConsumer<CreateEvent> with
        member this.Consume(context : ConsumeContext<CreateEvent>) =
            this.Handle(context.Message.ClusterId, context.CancellationToken)

type PostgresUpdatedConsumer(dbContext : MongoDbContext, searchService : ISearchService) =
    inherit PostgresProjectionConsumer(dbContext, searchService)

    member this.Handle(clusterId : string, cancellationToken : CancellationToken) =
        this.HandleUpsert(clusterId, searchService.Update, cancellationToken)

    interface IConsumer<UpdateEvent> with
        member this.Consume(context : ConsumeContext<UpdateEvent>) =
            this.Handle(context.Message.ClusterId, context.CancellationToken)

type PostgresDeletedConsumer(searchService : ISearchService) =
    member _.Handle(clusterId : string, cancellationToken : CancellationToken) =
        searchService.Delete(SearchDocumentType.PostgresCluster, clusterId) cancellationToken

    interface IConsumer<DeleteEvent> with
        member this.Consume(context : ConsumeContext<DeleteEvent>) =
            this.Handle(context.Message.ClusterId, context.CancellationToken)
