module Pole.Features.Postgres.Endpoints.Delete

open FluentValidation
open Microsoft.AspNetCore.Mvc
open MongoDB.Driver
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Events
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

                                    do! dbContext.StoreEventAsync(DeleteEvent(cluster.Id), session, ct)

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
