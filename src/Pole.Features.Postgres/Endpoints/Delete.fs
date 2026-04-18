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
                let filter = Builders<PostgresCluster>.Filter.Eq("Id", request.ClusterId)

                let! postgresCluster =
                    dbContext.Postgres
                        .Find(filter)
                        .FirstOrDefaultAsyncF(cancellationToken)

                match postgresCluster with
                | None ->
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    return!
                        dbContext.RunInTransactionAsync<DeletePostgresClusterResponse>(
                            (fun session ct ->
                                task {
                                    let! _ =
                                        dbContext.Postgres.DeleteOneAsync(
                                            session,
                                            filter,
                                            cancellationToken = ct
                                        )

                                    let event = DeleteEvent.create cluster.Id
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
