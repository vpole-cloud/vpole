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
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.ResourceDeployments.Contracts
open Pole.Mediator
open Pole.ResultType

type GetPostgresClusterConnectionStringResponse =
    { ConnectionString : string }

type GetPostgresClusterConnectionStringRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<GetPostgresClusterConnectionStringResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type GetPostgresClusterConnectionStringHandler(
    dbContext : IPostgresDbContext,
    credentialService : IPostgresCredentialService,
    loadDeployment : LoadResourceDeployment) =
    interface IMediatorHandler<GetPostgresClusterConnectionStringRequest, GetPostgresClusterConnectionStringResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let filter =
                    Builders<PostgresCluster>.Filter.And(
                        Builders<PostgresCluster>.Filter.ForTenant(request.TenantId),
                        Builders<PostgresCluster>.Filter.Eq((fun cluster -> cluster.Id), request.ClusterId))
                let! cluster =
                    dbContext.Postgres
                        .Find(filter)
                        .FirstOrNoneAsync(cancellationToken)

                match cluster with
                | None ->
                    return
                        Failures.NotFound("PostgreSQL cluster not found.", request.ClusterId)
                        |> ResultF.ofFailure
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        Failures.Conflict(
                            "A connection string is unavailable while the PostgreSQL cluster is being deleted.")
                        |> ResultF.ofFailure
                | Some cluster ->
                    let! placement =
                        loadDeployment.FindOptionAsync(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id,
                            cancellationToken)

                    match placement with
                    | None ->
                        return
                            Failures.Conflict(
                                "The PostgreSQL deployment has not applied its current desired generation.")
                            |> ResultF.ofFailure
                    | Some placement when
                        placement.Status <> "Applied" ||
                        placement.ObservedGeneration <> placement.DesiredGeneration ->
                        return
                            Failures.Conflict(
                                "The PostgreSQL deployment has not applied its current desired generation.")
                            |> ResultF.ofFailure
                    | Some placement ->
                        let endpoint =
                            placement.Endpoints
                            |> Seq.tryFind (fun item -> item.Kind = "PostgreSql")

                        match endpoint with
                        | None ->
                            return
                                Failures.Conflict(
                                    "The applied PostgreSQL deployment has no connection endpoint.")
                                |> ResultF.ofFailure
                        | Some endpoint ->
                            let! credentialResult =
                                credentialService.ResolveAsync(
                                    cluster.TenantId,
                                    cluster.Id,
                                    cancellationToken)

                            match credentialResult.TryGetValue() with
                            | struct (false, _, failure) ->
                                return failure |> ResultF.ofFailure
                            | struct (true, credentials, _) ->
                                let connectionString =
                                    $"Host={endpoint.Host};Port={endpoint.Port};Database={PostgresClusterKubernetesBuilder.buildDatabaseName cluster.Id};Username={credentials.Username};Password={credentials.Password};SSL Mode=Disable"

                                return
                                    { ConnectionString = connectionString }
                                    |> ResultF.ofValue
            }

type GetPostgresClusterConnectionStringRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClusterConnectionStringRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore

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
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
