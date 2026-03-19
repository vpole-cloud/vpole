namespace Pole.Features.Postgres.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open MassTransit
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType
open k8s

type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Instances : int option
      Storage : int option
      Username : string
      Password : string
      Databases : PostgresDatabase list option }

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody }
        interface IMediatorRequest<PostgresClusterDto>
    
type CreatePostgresClusterHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            let cluster = PostgresCluster.create request.Body.Name request.Body.GroupId request.Body.Username
                            { Id = None
                              Instances = request.Body.Instances
                              Storage = request.Body.Storage
                              Databases =  request.Body.Databases }
            dbContext.RunInTransactionAsync<PostgresClusterDto>(
                (fun session ct ->
                    task {
                        do! dbContext.Postgres.InsertOneAsync(session, cluster, cancellationToken = ct)
                        let event = CreateEvent.create cluster.Id request.Body.Password
                        
                        do! dbContext.StoreEventAsync(event, session, ct)

                        let response = PostgresCluster.toDto cluster
                        
                        return Result.Success(response) :> Result<PostgresClusterDto>
                    }
                ),
                cancellationToken = cancellationToken
            )
            
type CreatePostgresClusterValidator() as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore

[<AbstractClass; Sealed>]
type PostgresClusterCreateEndpoints =
    [<Extension>]
    static member MapCreatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<PostgresClusterCreateEndpoints>("/", nameof PostgresClusterCreateEndpoints.Handle, EndpointHttpMethod.Post)
            .WithName("CreatePostgresCluster")
        |> ignore
        endpoints

    static member Handle([<AsParameters>] request: CreatePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type PostgresClusterCreatedConsumer(
    database : MongoDbContext,
    kubernetes : IKubernetes) =
    interface IConsumer<CreateEvent> with
        member this.Consume(context) =
            task {
                let event = context.Message
                let! postgresCluster =
                    database.Postgres
                        .Find(Builders<PostgresCluster>.Filter.Eq("Id", event.ClusterId))
                        .FirstOrDefaultAsyncF(context.CancellationToken)

                match postgresCluster with
                | None -> ()
                | Some cluster ->
                    let builder = PostgresClusterKubernetesBuilder(cluster)
                    let clusterResource = builder.BuildCluster()
                    let clusterConfigMap = builder.BuildConfigMap()
                    let clusterSecret = builder.BuildSecret(event.Password)

                    do!
                        KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                            kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                                clusterResource,
                                group = "postgresql.cnpg.io",
                                version = "v1",
                                namespaceParameter = cluster.GroupId,
                                plural = "clusters",
                                cancellationToken = context.CancellationToken
                            )
                        )
                    do!
                        KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                            kubernetes.CoreV1.CreateNamespacedConfigMapAsync(
                                clusterConfigMap,
                                namespaceParameter = cluster.GroupId,
                                cancellationToken = context.CancellationToken
                            )
                        )
            }
