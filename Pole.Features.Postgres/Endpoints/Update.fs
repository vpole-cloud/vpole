module Pole.Features.Postgres.Endpoints.Update

open FluentValidation
open Microsoft.AspNetCore.Mvc
open MongoDB.Driver
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Postgres.Common
open Pole.Features.Postgres.Events
open Pole.Mediator
open Pole.ResultType

type UpdatePostgresClusterRequestBody =
    { Name : string option
      GroupId : string option
      Instances : int option
      Storage : int option
      Databases : PostgresDatabase list option }

type UpdatePostgresClusterResponse =
    { Id : string }

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterRequestBody }
    interface IMediatorRequest<UpdatePostgresClusterResponse>

type UpdatePostgresClusterHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<UpdatePostgresClusterRequest, UpdatePostgresClusterResponse> with
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
                    let updatedCluster =
                        { cluster with
                            Name = defaultArg request.Body.Name cluster.Name
                            GroupId = defaultArg request.Body.GroupId cluster.GroupId
                            Spec =
                                { Instances = defaultArg request.Body.Instances cluster.Spec.Instances
                                  Storage = defaultArg request.Body.Storage cluster.Spec.Storage }
                            Databases = defaultArg request.Body.Databases cluster.Databases }

                    return!
                        dbContext.RunInTransactionAsync<UpdatePostgresClusterResponse>(
                            (fun session ct ->
                                task {
                                    let! _ =
                                        dbContext.Postgres.ReplaceOneAsync(
                                            session,
                                            filter,
                                            updatedCluster,
                                            cancellationToken = ct
                                        )

                                    do! dbContext.StoreEventAsync(UpdateEvent(cluster.Id), session, ct)

                                    return { Id = request.ClusterId } |> ResultF.ofValue
                                }
                            ),
                            cancellationToken = cancellationToken
                        )
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty().WithMessage("Cluster ID must not be empty.") |> ignore
