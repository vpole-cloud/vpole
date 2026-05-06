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
open Pole.Features.Events.FSharp.Postgres
open Pole.Features.Postgres.Kubernetes
open Pole.Mediator
open Pole.ResultType

type DeletePostgresClusterResponse =
    { Id : string }

type DeletePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string }
    interface IMediatorRequest<DeletePostgresClusterResponse>

type DeletePostgresClusterHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<DeletePostgresClusterRequest, DeletePostgresClusterResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! postgresCluster =
                    dbContext.Postgres
                        .Find(fun c -> c.Id = request.ClusterId)
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
                                            (fun c -> c.Id = request.ClusterId),
                                            cancellationToken = ct
                                        )
                                    if deleteResult.DeletedCount = 0L then
                                        return Result.NotFound($"PostgreSQL cluster with ID '{request.ClusterId}' not found.")
                                               |> ResultF.ofFailure
                                    else
                                        let event = DeleteEvent.create request.ClusterId cluster.Spec.KubernetesNamespace cluster.Spec.DeploymentMode
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
                context.Message.NamespaceName,
                context.Message.DeploymentMode,
                context.CancellationToken)
