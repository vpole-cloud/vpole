namespace Pole.Features.Postgres.Application.Clusters

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain

type PostgresClusterListQuery =
    { TenantId: string
      PageNumber: int
      PageSize: int }

    member this.Skip = (this.PageNumber - 1) * this.PageSize

type PostgresClusterPage =
    { Items: IReadOnlyList<PostgresCluster>
      TotalCount: int64 }

type LoadPostgresClusterPage =
    delegate of query: PostgresClusterListQuery * cancellationToken: CancellationToken -> Task<PostgresClusterPage>

type MongoLoadPostgresClusterPage(clusters: IMongoCollection<PostgresCluster>) =
    member _.InvokeAsync(query: PostgresClusterListQuery, cancellationToken: CancellationToken) =
        task {
            if isNull (box query) then
                nullArg "query"

            PostgresPersistenceGuards.listQuery query.TenantId query.PageNumber query.PageSize
            let clustersVisibleToTenant = PostgresFilters.forList query.TenantId

            let! totalCount =
                clusters.CountDocumentsAsync(clustersVisibleToTenant, cancellationToken = cancellationToken)

            let! items =
                clusters
                    .Find(clustersVisibleToTenant)
                    .Skip(query.Skip)
                    .Limit(query.PageSize)
                    .ToListAsync(cancellationToken)

            return
                { Items = items :> IReadOnlyList<PostgresCluster>
                  TotalCount = totalCount }
        }
