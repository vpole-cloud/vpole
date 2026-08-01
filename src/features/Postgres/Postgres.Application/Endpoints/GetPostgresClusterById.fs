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
open Pole.Features.ResourceDeployments.Contracts
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
    
type GetPostgresClusterHandler(
    dbContext : IPostgresDbContext,
    deploymentQuery : IResourceDeploymentQueryService) =
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
                    return Failures.NotFound("Postgres cluster not found.", request.ClusterId)
                           |> ResultF.ofFailure
                | Some cluster ->
                    let! placement =
                        deploymentQuery.FindOptionAsync(
                            cluster.TenantId,
                            ResourceDeploymentResourceTypes.PostgreSql,
                            cluster.Id,
                            cancellationToken)

                    match placement with
                    | None ->
                        return
                            Failures.Conflict(
                                "The PostgreSQL resource deployment is unavailable.")
                            |> ResultF.ofFailure
                    | Some placement ->
                        return
                            PostgresClusterDto.create cluster placement
                            |> ResultF.ofValue
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
