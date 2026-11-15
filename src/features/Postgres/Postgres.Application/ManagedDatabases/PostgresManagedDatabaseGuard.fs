namespace Pole.Features.Postgres.Application.ManagedDatabases

open System.Threading
open MongoDB.Driver
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain

/// Dependency reads join the same cluster-fenced transaction as configuration/lifecycle writes.
type PostgresManagedDatabaseGuard(claims: IMongoCollection<PostgresManagedDatabase>) =
    member _.HasLiveDependenciesAsync(tenantId, clusterId, session: IClientSessionHandle, ct: CancellationToken) =
        task {
            let! claim = claims.Find(session, PostgresDatabaseFilters.liveForCluster tenantId clusterId)
                            .Limit(1).FirstOrDefaultAsync(ct)
            return not (isNull (box claim))
        }
