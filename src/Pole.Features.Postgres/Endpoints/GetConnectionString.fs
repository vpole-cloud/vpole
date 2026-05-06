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

type GetPostgresClusterConnectionStringResponse =
    { ConnectionString : string }

type GetPostgresClusterConnectionStringRequest =
    { [<FromRoute>]
      ClusterId : string }
    interface IMediatorRequest<GetPostgresClusterConnectionStringResponse>

type GetPostgresClusterConnectionStringHandler(
    dbContext : MongoDbContext,
    secretStore : IPostgresClusterSecretStore) =
    interface IMediatorHandler<GetPostgresClusterConnectionStringRequest, GetPostgresClusterConnectionStringResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! postgresCluster =
                    dbContext.Postgres
                        .Find(fun c -> c.Id = request.ClusterId)
                        .FirstOrNoneAsync(cancellationToken)

                match postgresCluster with
                | None ->
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, cancellationToken)

                    match credentials with
                    | None ->
                        return Result.NotFound($"Credentials secret for Postgres cluster '{cluster.Id}' was not found.")
                               |> ResultF.ofFailure
                    | Some credentials ->
                        let connectionString =
                            $"Host={PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id}.{cluster.Spec.KubernetesNamespace}.svc.cluster.local;Port=5432;Database={PostgresClusterKubernetesBuilder.buildDatabaseName cluster.Id};Username={credentials.Username};Password={credentials.Password};SSL Mode=Disable"

                        return { ConnectionString = connectionString } |> ResultF.ofValue
            }

type GetPostgresClusterConnectionStringRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClusterConnectionStringRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore

[<AbstractClass; Sealed>]
type GetPostgresClusterConnectionStringEndpoint =
    [<Extension>]
    static member MapGetPostgresClusterConnectionStringEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresClusterConnectionStringEndpoint>("/{ClusterId}/connection-string", nameof GetPostgresClusterConnectionStringEndpoint.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusterConnectionString")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresClusterConnectionStringRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
