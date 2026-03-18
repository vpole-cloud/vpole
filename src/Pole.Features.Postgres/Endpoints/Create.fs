module Pole.Features.Postgres.Endpoints.Create

open FluentValidation
open Microsoft.AspNetCore.Mvc
open Pole.Database
open Pole.Domain.Models.FSharp
open Pole.Features.Postgres.Events
open Pole.Features.Postgres.Common
open Pole.Mediator
open Pole.ResultType
 
type CreatePostgresClusterRequestBody =
    { Name : string
      GroupId : string
      Instances : int option
      Storage : int option
      Databases : PostgresDatabase list option }

type CreatePostgresClusterRequest =
    { [<FromBody>] Body : CreatePostgresClusterRequestBody }
        interface IMediatorRequest<PostgresClusterDto>
    
type CreatePostgresClusterHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<CreatePostgresClusterRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
            let cluster = PostgresCluster.create request.Body.Name request.Body.GroupId
                            { Id = None
                              Instances = request.Body.Instances
                              Storage = request.Body.Storage
                              Databases =  request.Body.Databases }
            dbContext.RunInTransactionAsync<PostgresClusterDto>(
                (fun session ct ->
                    task {
                        do! dbContext.Postgres.InsertOneAsync(session, cluster, cancellationToken = ct)
                        do! dbContext.StoreEventAsync(CreateEvent(cluster.Id), session, ct)

                        let response = PostgresCluster.toDto cluster
                        
                        return Result.Success(response) :> Result<PostgresClusterDto>
                    }
                ),
                cancellationToken = cancellationToken
            )
            
type CreatePostgresClusterValidator() as this =
    inherit AbstractValidator<CreatePostgresClusterRequest>()
    do
        this.RuleFor(_.Body.Name).NotEmpty().WithMessage("Name is required.") |> ignore
        this.RuleFor(_.Body.GroupId).NotEmpty().WithMessage("GroupId is required.") |> ignore
