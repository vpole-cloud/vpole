module Pole.Features.Postgres.Endpoints.GetById

open FluentValidation
open Microsoft.AspNetCore.Mvc
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Common
open Pole.Mediator
open Pole.ResultType

type GetByIdPostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string }
    interface IMediatorRequest<PostgresClusterDto>
    
type GetPostgresClusterHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<GetByIdPostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            task {
                let! postgresCluster =
                    dbContext.Postgres
                        .Find(fun x -> x.Id = request.ClusterId)
                        .FirstOrDefaultAsyncF(cancellationToken)

                match postgresCluster with
                | None ->
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    return PostgresCluster.toDto cluster |> ResultF.ofValue
            }
            
type GetByIdPostgresClusterRequestValidator() as this =
    inherit AbstractValidator<GetByIdPostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty().WithMessage("Cluster ID must not be empty.") |> ignore