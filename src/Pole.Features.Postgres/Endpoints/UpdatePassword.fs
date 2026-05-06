namespace Pole.Features.Postgres.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType

type UpdatePostgresClusterPasswordRequestBody =
    { Password : string }

type UpdatePostgresClusterPasswordResponse =
    { Id : string }

type UpdatePostgresClusterPasswordRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterPasswordRequestBody }
    interface IMediatorRequest<UpdatePostgresClusterPasswordResponse>

type UpdatePostgresClusterPasswordHandler(
    dbContext : MongoDbContext,
    secretStore : IPostgresClusterSecretStore) =
    interface IMediatorHandler<UpdatePostgresClusterPasswordRequest, UpdatePostgresClusterPasswordResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! postgresCluster =
                    dbContext.Postgres
                        .Find(Builders<PostgresCluster>.Filter.Eq("Id", request.ClusterId))
                        .FirstOrNoneAsync(cancellationToken)

                match postgresCluster with
                | None ->
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    match cluster.Spec.DeploymentMode with
                    | PostgresDeploymentMode.Operator ->
                        do! secretStore.UpdatePasswordAsync(cluster.Spec.KubernetesNamespace, cluster.Id, request.Body.Password, cancellationToken)
                        return { Id = cluster.Id } |> ResultF.ofValue
                    | PostgresDeploymentMode.StatefulSet ->
                        return Result.Conflict("Password rotation is not supported for StatefulSet PostgreSQL deployments.")
                               |> ResultF.ofFailure
                    | _ ->
                        return Result.Conflict($"Unsupported PostgreSQL deployment mode '{cluster.Spec.DeploymentMode}'.")
                               |> ResultF.ofFailure
            }

type UpdatePostgresClusterPasswordRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterPasswordRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore
        this.RuleFor(_.Body.Password).NotEmpty() |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresClusterPasswordEndpoint =
    [<Extension>]
    static member MapUpdatePostgresClusterPasswordEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<UpdatePostgresClusterPasswordEndpoint>("/{ClusterId}/password", nameof UpdatePostgresClusterPasswordEndpoint.Handle, EndpointHttpMethod.Patch)
            .WithName("UpdatePostgresClusterPassword")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: UpdatePostgresClusterPasswordRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
