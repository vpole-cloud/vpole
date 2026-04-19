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
                        .FirstOrNoneAsync(cancellationToken)

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

[<AbstractClass; Sealed>]
type PostgresClusterGetByIdEndpoints =
    [<Extension>]
    static member MapGetByIdPostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<PostgresClusterGetByIdEndpoints>("/{ClusterId}", nameof PostgresClusterGetByIdEndpoints.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusterById")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetByIdPostgresClusterRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
