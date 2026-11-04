namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.ResourceDeployments.Contracts
open Pole.Mediator
open Pole.Validation

type GetPostgresClusterConnectionStringResponse =
    { ConnectionString : string }

type GetPostgresClusterConnectionStringOutcome =
    | PostgresClusterConnectionString of GetPostgresClusterConnectionStringResponse
    | GetPostgresClusterConnectionStringRejected of GetPostgresClusterConnectionStringError

type GetPostgresClusterConnectionStringRequest =
    { [<FromRoute>]
      ClusterId : string
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId : string }
    interface IMediatorRequest<GetPostgresClusterConnectionStringOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type GetPostgresClusterConnectionStringHandler(
    loadCluster : LoadPostgresClusterById,
    resolveCredentials : ResolvePostgresClusterCredentials,
    loadDeployment : LoadResourceDeployment) =
    interface IMediatorOutcomeHandler<GetPostgresClusterConnectionStringRequest, GetPostgresClusterConnectionStringOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! cluster =
                    loadCluster.Invoke(
                        request.TenantId,
                        request.ClusterId,
                        cancellationToken)

                match cluster with
                | None ->
                    return
                        GetPostgresClusterConnectionStringRejected(
                            ConnectionStringState(
                                PostgresClusterConnectionStringNotFound request.ClusterId))
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        GetPostgresClusterConnectionStringRejected(
                            ConnectionStringState(
                                PostgresClusterConnectionStringDeleting cluster.Id))
                | Some cluster ->
                    let! placement =
                        loadDeployment.Invoke(
                            LoadResourceDeploymentRequest(
                                cluster.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                cluster.Id),
                            cancellationToken)

                    if isNull placement ||
                       placement.Status <> ResourceDeploymentStatus.Applied ||
                       placement.ObservedGeneration <> placement.DesiredGeneration then
                        return
                            GetPostgresClusterConnectionStringRejected(
                                ConnectionStringState(
                                    PostgresClusterDeploymentNotReady cluster.Id))
                    else
                        let endpoint =
                            placement.Endpoints
                            |> Seq.tryFind (fun item -> item.Kind = "PostgreSql")

                        match endpoint with
                        | None ->
                            return
                                GetPostgresClusterConnectionStringRejected(
                                    ConnectionStringState(
                                        PostgresClusterConnectionEndpointUnavailable cluster.Id))
                        | Some endpoint ->
                            let! credentialResult =
                                resolveCredentials.Invoke(
                                    cluster.TenantId,
                                    cluster.Id,
                                    cancellationToken)

                            match credentialResult with
                            | PostgresError error ->
                                return
                                    GetPostgresClusterConnectionStringRejected(
                                        ConnectionStringCredentials error)
                            | PostgresSuccess credentials ->
                                let connectionString =
                                    $"Host={endpoint.Host};Port={endpoint.Port};Database={PostgresClusterKubernetesBuilder.buildDatabaseName cluster.Id};Username={credentials.Username};Password={credentials.Password};SSL Mode=Disable"

                                return
                                    { ConnectionString = connectionString }
                                    |> PostgresClusterConnectionString
            }

type GetPostgresClusterConnectionStringRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClusterConnectionStringRequest>()
    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore

[<AbstractClass; Sealed>]
type GetPostgresClusterConnectionString =
    [<Extension>]
    static member MapGetPostgresClusterConnectionStringEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresClusterConnectionString>(
            "/{ClusterId}/connection-string",
            nameof GetPostgresClusterConnectionString.Handle,
            EndpointHttpMethod.Get)
            .WithName("GetPostgresClusterConnectionString")
        |> ignore
        endpoints

    static member Handle(
        [<AsParameters>] request : GetPostgresClusterConnectionStringRequest,
        [<FromServices>] mediator : IMediator,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterConnectionString value -> Results.Ok(value)
                    | GetPostgresClusterConnectionStringRejected error ->
                        (PostgresHttpMappings.mapConnectionStringError error).ToHttpResult())
        }
