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
    interface ITenantReadOperation with
        member this.TenantId = this.TenantId

type GetPostgresClusterConnectionStringHandler(
    dbContext : IPostgresDbContext,
    secretStore : IPostgresClusterSecretStore) =
    interface IMediatorHandler<GetPostgresClusterConnectionStringRequest, GetPostgresClusterConnectionStringResponse> with
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
                    return Failures.NotFound("Postgres cluster not found.", request.ClusterId)
                           |> ResultF.ofFailure
                | Some cluster ->
                    let! credentials = secretStore.GetAsync(cluster.Spec.KubernetesNamespace, cluster.Id, cancellationToken)

                    match credentials with
                    | None ->
                        return Failures.NotFound("Credentials secret for Postgres cluster was not found.", cluster.Id)
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
type GetPostgresClusterConnectionString =
    [<Extension>]
    static member MapGetPostgresClusterConnectionStringEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresClusterConnectionString>("/{ClusterId}/connection-string", nameof GetPostgresClusterConnectionString.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusterConnectionString")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresClusterConnectionStringRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

