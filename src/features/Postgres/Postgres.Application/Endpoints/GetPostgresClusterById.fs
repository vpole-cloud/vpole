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
open Pole.Mediator
open Pole.ResultType

type GetPostgresClusterByIdRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<PostgresClusterDto>
    interface ITenantReadOperation with
        member this.TenantId = this.TenantId
    
type GetPostgresClusterHandler(dbContext : IPostgresDbContext) =
    interface IMediatorHandler<GetPostgresClusterByIdRequest, PostgresClusterDto> with
        member this.HandleAsync(request, cancellationToken) =
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
                    return Result.NotFound($"Postgres cluster with ID '{request.ClusterId}' not found.")
                           |> ResultF.ofFailure
                | Some cluster ->
                    return PostgresCluster.toDto cluster |> ResultF.ofValue
            }
            
type GetPostgresClusterByIdRequestValidator() as this =
    inherit AbstractValidator<GetPostgresClusterByIdRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty().WithMessage("Cluster ID must not be empty.") |> ignore

[<AbstractClass; Sealed>]
type GetPostgresClusterById =
    [<Extension>]
    static member MapGetByIdPostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresClusterById>("/{ClusterId}", nameof GetPostgresClusterById.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusterById")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresClusterByIdRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }

