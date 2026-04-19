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
open Pole.Features.Events.FSharp.Postgres
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType
open k8s
open Pole.Domain.Models

type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Instances : int option
      Storage : int option
      Username : string
      Password : string }

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody }
        interface IMediatorRequest<PostgresClusterDto>
    
type CreatePostgresClusterHandler(dbContext : MongoDbContext, secretStore : IPostgresClusterSecretStore) =
    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            task {
                let! resourceGroup =
                    dbContext.ResourceGroups
                        .Find(fun c -> c.Id = request.Body.GroupId)
                        .FirstOrNoneAsync(cancellationToken)

                match resourceGroup with
                | None ->
                    return Result.NotFound($"Resource group with ID '{request.Body.GroupId}' not found.")
                           |> ResultF.ofFailure
                | Some group ->
                    let cluster = PostgresCluster.create request.Body.Name request.Body.GroupId group.KubernetesNamespace
                                   { PostgresCluster.emptyOptions with
                                        Instances = defaultArg request.Body.Instances PostgresCluster.emptyOptions.Instances
                                        Storage = defaultArg request.Body.Storage PostgresCluster.emptyOptions.Storage }

                    return!
                        dbContext.RunInTransactionAsync<PostgresClusterDto>(
                            (fun session ct ->
                                task {
                                    do! dbContext.Postgres.InsertOneAsync(session, cluster, cancellationToken = ct)
                                    do! secretStore.CreateAsync(group.KubernetesNamespace, cluster.Id, request.Body.Username, request.Body.Password, ct)
                                    let event = CreateEvent.create cluster.Id
                                    
                                    do! dbContext.StoreEventAsync(event, session, ct)

                                    let response = PostgresCluster.toDto cluster
                                    
                                    return Result.Success(response) :> Result<PostgresClusterDto>
                                }
                            ),
                            cancellationToken = cancellationToken
                        )
            }
            
type CreatePostgresClusterValidator() as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore
        this.RuleFor(_.Body.Username).NotEmpty().WithMessage("Username is required.") |> ignore
        this.RuleFor(_.Body.Password).NotEmpty().WithMessage("Password is required.") |> ignore

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
    kubernetes : IKubernetes,
    secretStore : IPostgresClusterSecretStore) =
    interface IConsumer<CreateEvent> with
        member this.Consume(context) =
            task {
                let event = context.Message
                let! postgresCluster =
                    database.Postgres
                        .Find(fun c -> c.Id = event.ClusterId)
                        .FirstOrNoneAsync(context.CancellationToken)

                match postgresCluster with
                | None -> ()
                | Some cluster ->
                    let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, context.CancellationToken)

                    match credentials with
                    | None -> ()
                    | Some credentials ->
                        let builder = PostgresClusterKubernetesBuilder(cluster, credentials.Username)
                        do! PostgresClusterKubernetesResourceManager.CreateAsync(kubernetes, builder, context.CancellationToken)
            }
