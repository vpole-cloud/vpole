namespace Pole.Features.Postgres.Endpoints

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
open Pole.Features.Postgres.Common
open Pole.Features.Request
open Pole.Mediator

type GetPostgresClustersRequest() =
    interface IMediatorRequest<PaginatedResponse<PostgresClusterDto>>

    [<FromQuery; DefaultValue(1); Description("The page number to retrieve (1-based).")>]
    member val PageNumber = 1 with get, set

    [<FromQuery; DefaultValue(10); Description("The number of items per page.")>]
    member val PageSize = 10 with get, set

type GetPostgresClustersHandler(dbContext : MongoDbContext) =
    interface IMediatorHandler<GetPostgresClustersRequest, PaginatedResponse<PostgresClusterDto>> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let filter = FilterDefinition<_>.Empty
                let skip = (request.PageNumber - 1) * request.PageSize

                let! totalCount =
                    dbContext.Postgres.CountDocumentsAsync(filter, cancellationToken = cancellationToken)

                let! clusters =
                    dbContext.Postgres
                        .Find(filter)
                        .Skip(skip)
                        .Limit(request.PageSize)
                        .ToListAsync(cancellationToken)

                return
                    PaginatedResponse<PostgresClusterDto>(
                        Items = (clusters |> Seq.map PostgresCluster.toDto),
                        TotalCount = totalCount,
                        PageNumber = request.PageNumber,
                        PageSize = request.PageSize
                    )
                    |> ResultF.ofValue
            }

type GetPostgresClustersRequestValidator() =
    inherit AbstractValidator<GetPostgresClustersRequest>()

[<AbstractClass; Sealed>]
type PostgresClusterGetEndpoints =
    [<Extension>]
    static member MapGetPostgresClustersEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints.MapMethod<PostgresClusterGetEndpoints>("/", nameof PostgresClusterGetEndpoints.Handle, EndpointHttpMethod.Get)
            .WithName("GetPostgresClusters")
            |> ignore
        endpoints

    static member Handle([<AsParameters>] request: GetPostgresClustersRequest, [<FromServices>] mediator: IMediator) =
        task {
            let! result = mediator.Send request
            return result.ToHttpResult(fun value -> Results.Ok(value))
        }
