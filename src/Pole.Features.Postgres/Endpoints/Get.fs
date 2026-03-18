module Pole.Features.Postgres.Endpoints.Get

open System.ComponentModel
open Microsoft.AspNetCore.Mvc
open FluentValidation
open MongoDB.Driver
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
