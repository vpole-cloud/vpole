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
                    return Failures.NotFound("PostgreSQL cluster not found.", request.ClusterId)
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
                                        return Failures.NotFound("PostgreSQL cluster not found.", request.ClusterId)
                                               |> ResultF.ofFailure
                                    else
                                        let event = PostgresClusterEvents.deleted cluster
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
type DeletePostgresCluster =
    [<Extension>]
    static member MapDeletePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<DeletePostgresCluster>("/{ClusterId}", nameof DeletePostgresCluster.Handle, EndpointHttpMethod.Delete)
            .WithName("DeletePostgresCluster")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: DeletePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

type DeletePostgresClusterDeploymentOnPostgresClusterDeletedConsumer(deploymentManager : IPostgresClusterDeploymentManager) =
    interface MassTransit.IConsumer<PostgresClusterDeletedEvent> with
        member _.Consume(context) =
            deploymentManager.DeleteAsync(
                context.Message.ClusterId,
                context.Message.Spec.KubernetesNamespace,
                PostgresClusterEvents.deploymentModeFromSnapshot context.Message.Spec.DeploymentMode,
                context.CancellationToken)

