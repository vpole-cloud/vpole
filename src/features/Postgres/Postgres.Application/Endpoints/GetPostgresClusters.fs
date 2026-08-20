namespace Pole.Features.Postgres.Application.Endpoints

open System.ComponentModel
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Mvc
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Request
open Pole.Mediator

type GetPostgresClustersRequest() =
    interface IMediatorRequest<PaginatedResponse<PostgresClusterDto>>
    interface ITenantReadOperation with
        member this.TenantId = this.TenantId

    [<FromHeader(Name = TenantHeaders.TenantId)>]
    member val TenantId = "" with get, set

    [<FromQuery; DefaultValue(1); Description("The page number to retrieve (1-based).")>]
    member val PageNumber = 1 with get, set

    [<FromQuery; DefaultValue(10); Description("The number of items per page.")>]
    member val PageSize = 10 with get, set

type GetPostgresClustersHandler(
    dbContext : IPostgresDbContext,
    loadDeployments : LoadResourceDeployments) =
    interface IMediatorHandler<GetPostgresClustersRequest, PaginatedResponse<PostgresClusterDto>> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let filter = Builders<PostgresCluster>.Filter.ForTenant(request.TenantId)
                let skip = (request.PageNumber - 1) * request.PageSize

                let! totalCount =
                    dbContext.Postgres.CountDocumentsAsync(filter, cancellationToken = cancellationToken)

                let! clusters =
                    dbContext.Postgres
                        .Find(filter)
                        .Skip(skip)
                        .Limit(request.PageSize)
                        .ToListAsync(cancellationToken)

                let clusterIds =
                    clusters
                    |> Seq.map _.Id
                    |> Seq.toArray
                let! placements =
                    loadDeployments.Invoke(
                        request.TenantId,
                        PostgresResourceDeployment.ResourceType,
                        clusterIds,
                        cancellationToken)

                let toDto (cluster : PostgresCluster) =
                    match placements.TryGetValue(cluster.Id) with
                    | true, placement ->
                        PostgresClusterDto.create cluster placement
                    | false, _ ->
                        invalidOp
                            $"PostgreSQL cluster '{cluster.Id}' has no resource deployment."

                return
                    PaginatedResponse<PostgresClusterDto>(
                        Items = (clusters |> Seq.map toDto),
                        TotalCount = totalCount,
                        PageNumber = request.PageNumber,
                        PageSize = request.PageSize
                    )
                    |> ResultF.ofValue
            }

type GetPostgresClustersRequestValidator() =
    inherit AbstractValidator<GetPostgresClustersRequest>()

[<AbstractClass; Sealed>]
type GetPostgresClusters =
    [<Extension>]
    static member MapGetPostgresClustersEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<GetPostgresClusters>("/", nameof GetPostgresClusters.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusters")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresClustersRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
