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
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType

type DeletePostgresClusterResponse =
    { Id : string }

type DeletePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<DeletePostgresClusterResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type DeletePostgresClusterHandler(dbContext : IPostgresDbContext) =
    interface IMediatorHandler<DeletePostgresClusterRequest, DeletePostgresClusterResponse> with
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
                    return Result.NotFound($"PostgreSQL cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    return!
                        dbContext.RunInTransactionAsync<DeletePostgresClusterResponse>(
                            (fun session ct ->
                                task {
                                    let! deleteResult =
                                        dbContext.Postgres.DeleteOneAsync(
                                            session,
                                            filter,
                                            cancellationToken = ct
                                        )
                                    if deleteResult.DeletedCount = 0L then
                                        return Result.NotFound($"PostgreSQL cluster with ID '{request.ClusterId}' not found.")
                                               |> ResultF.ofFailure
                                    else
                                        let event = DeleteEvent.create cluster
                                        do! dbContext.StoreEventAsync(event, session, ct)
                                        return { Id = request.ClusterId } |> ResultF.ofValue
                                }
                            ),
                            cancellationToken = cancellationToken
                        )
            }

type DeletePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<DeletePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty().WithMessage("Cluster ID must not be empty.") |> ignore

[<AbstractClass; Sealed>]
type PostgresClusterDeleteEndpoints =
    [<Extension>]
    static member MapDeletePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<PostgresClusterDeleteEndpoints>("/{ClusterId}", nameof PostgresClusterDeleteEndpoints.Handle, EndpointHttpMethod.Delete)
            .WithName("DeletePostgresCluster")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: DeletePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type PostgresClusterDeletedConsumer(deploymentManager : IPostgresClusterDeploymentManager) =
    interface MassTransit.IConsumer<DeleteEvent> with
        member _.Consume(context) =
            deploymentManager.DeleteAsync(
                context.Message.ClusterId,
                context.Message.Spec.KubernetesNamespace,
                context.Message.Spec.DeploymentMode,
                context.CancellationToken)
