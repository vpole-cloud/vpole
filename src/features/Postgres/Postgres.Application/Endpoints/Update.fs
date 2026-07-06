namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Mediator
open Pole.ResultType

type UpdatePostgresClusterRequestBody =
    { Instances : int option
      Storage : int option }

type UpdatePostgresClusterResponse =
    { Id : string }

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type UpdatePostgresClusterHandler(dbContext : IPostgresDbContext) =
    interface IMediatorHandler<UpdatePostgresClusterRequest, UpdatePostgresClusterResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let filter =
                    Builders<PostgresCluster>.Filter.And(
                        Builders<PostgresCluster>.Filter.ForTenant(request.TenantId),
                        Builders<PostgresCluster>.Filter.Eq(_.Id, request.ClusterId))

                let! postgresCluster =
                    dbContext.Postgres
                        .Find(filter)
                        .FirstOrNoneAsync(cancellationToken)

                match postgresCluster with
                | None ->
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    let updatedInstances = defaultArg request.Body.Instances cluster.Spec.Instances

                    if cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet && updatedInstances <> 1 then
                        let validationErrors = System.Collections.Generic.Dictionary<string, string[]>()
                        validationErrors["Body.Instances"] <- [| "StatefulSet PostgreSQL supports exactly one instance." |]
                        return Result.ValidationFailure(validationErrors) |> ResultF.ofFailure
                    else
                        let updatedCluster =
                            { cluster with
                                GroupId = cluster.GroupId
                                Spec =
                                    { Instances = updatedInstances
                                      Storage = defaultArg request.Body.Storage cluster.Spec.Storage
                                      KubernetesNamespace = cluster.Spec.KubernetesNamespace
                                      Version = cluster.Spec.Version
                                      DeploymentMode = cluster.Spec.DeploymentMode } }

                        return!
                            dbContext.RunInTransactionAsync<UpdatePostgresClusterResponse>(
                                (fun session ct ->
                                    task {
                                        let! _ =
                                            dbContext.Postgres.ReplaceOneAsync(
                                                session,
                                                filter,
                                                updatedCluster,
                                                cancellationToken = ct
                                            )

                                        let event = PostgresClusterEvents.updated cluster updatedCluster
                                        do! dbContext.StoreEventAsync(event, session, ct)

                                        return { Id = request.ClusterId } |> ResultF.ofValue
                                    }
                                ),
                                cancellationToken = cancellationToken
                            )
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId)
            .NotEmpty()
        |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresClusterEndpoint =
    [<Extension>]
    static member MapUpdatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<UpdatePostgresClusterEndpoint>("/{ClusterId}", nameof UpdatePostgresClusterEndpoint.Handle, EndpointHttpMethod.Put)
            .WithName("UpdatePostgresCluster")
            |> ignore
        endpoints
        
    static member Handle([<AsParameters>] request: UpdatePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type PostgresClusterUpdatedConsumer(
    secretStore : IPostgresClusterSecretStore,
    deploymentManager : IPostgresClusterDeploymentManager) =
    interface MassTransit.IConsumer<PostgresClusterUpdatedEvent> with
        member _.Consume(context) =
            task {
                let cluster = PostgresClusterEvents.fromUpdated context.Message
                let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, context.CancellationToken)
                match credentials with
                | None -> ()
                | Some credentials ->
                    do! deploymentManager.UpdateAsync(cluster, credentials.Username, context.CancellationToken)
            }

