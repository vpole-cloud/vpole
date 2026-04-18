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

type UpdatePostgresClusterRequestBody =
    { Name : string option
      GroupId : string option
      Instances : int option
      Storage : int option
      Username : string option
      Password : string option }

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
                            Username = defaultArg request.Body.Username cluster.Username
                            Spec =
                                { Instances = defaultArg request.Body.Instances cluster.Spec.Instances
                                  Storage = defaultArg request.Body.Storage cluster.Spec.Storage } }

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

                                    let event = UpdateEvent.create cluster.Id request.Body.Password
                                    do! dbContext.StoreEventAsync(event, session, ct)

                                    return { Id = request.ClusterId } |> ResultF.ofValue
                                }
                            ),
                            cancellationToken = cancellationToken
                        )
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId)
            .NotEmpty()
        |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresClusterEndpoint =
    [<Extension>]
    static member MapUpdatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<UpdatePostgresClusterEndpoint>("/{ClusterId}", nameof UpdatePostgresClusterEndpoint.Handle, EndpointHttpMethod.Put)
            .WithName("UpdatePostgresCluster")
            |> ignore
        endpoints
        
    static member Handle([<AsParameters>] request: UpdatePostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
