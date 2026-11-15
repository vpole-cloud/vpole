namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Secrets.Contracts

type PostgresManagedDatabaseStateStore
    (claims: IMongoCollection<PostgresManagedDatabase>, clusters: IMongoCollection<PostgresCluster>,
     transactions: MongoRunTransaction, releaseSecret: ReleaseManagedSecret, clock: TimeProvider) =

    member _.LoadAsync(tenantId, owner, ct: CancellationToken) =
        claims.Find(PostgresDatabaseIdentity.ownerFilter tenantId owner).FirstOrDefaultAsync(ct)

    member _.BeginPurgeAsync(tenantId, owner, ct: CancellationToken) =
        transactions.InvokeOutcomeAsync<PostgresManagedDatabase option>(
            Func<MongoTransactionContext<PostgresManagedDatabase option>, CancellationToken, Task<MongoTransactionDecision<PostgresManagedDatabase option>>>(fun tx token ->
                task {
                    let! current = claims.Find(tx.Session, PostgresDatabaseIdentity.ownerFilter tenantId owner).FirstOrDefaultAsync(token)
                    if isNull (box current) then return tx.Commit(None)
                    elif not current.IsActive then return tx.Commit(Some current)
                    else
                        let updated = current.BeginPurge(clock.GetUtcNow().UtcDateTime)
                        let! written = claims.ReplaceOneAsync(tx.Session, PostgresDatabaseFilters.atRevision current, updated, cancellationToken = token)
                        if written.ModifiedCount <> 1L then return tx.Abort(None)
                        else
                            let! _ = clusters.UpdateOneAsync(tx.Session, PostgresFilters.byId tenantId current.ClusterId,
                                                Builders<PostgresCluster>.Update.Inc((fun item -> item.PlanningFence), 1L), cancellationToken = token)
                            return tx.Commit(Some updated)
                }), ct)

    member _.CompletePurgeAsync(expected: PostgresManagedDatabase, ct: CancellationToken) =
        transactions.InvokeOutcomeAsync<bool>(
            Func<MongoTransactionContext<bool>, CancellationToken, Task<MongoTransactionDecision<bool>>>(fun tx token ->
                task {
                    let! current = claims.Find(tx.Session, PostgresDatabaseFilters.identity expected.TenantId expected.Id).FirstOrDefaultAsync(token)
                    if isNull (box current) then return tx.Abort(false)
                    elif current.IsPurged then return tx.Commit(true)
                    elif current.IsActive || current.Revision <> expected.Revision then return tx.Abort(false)
                    else
                        let! released = releaseSecret.Invoke(ReleaseManagedSecretRequest(current.TenantId, PostgresDatabaseIdentity.credentialOwner current), tx.Session, token)
                        if not (released.Value :? ManagedSecretMutationSucceeded) then return tx.Abort(false)
                        else
                            let updated = current.CompletePurge(clock.GetUtcNow().UtcDateTime)
                            let! written = claims.ReplaceOneAsync(tx.Session, PostgresDatabaseFilters.atRevision current, updated, cancellationToken = token)
                            if written.ModifiedCount <> 1L then return tx.Abort(false)
                            else
                                let! _ = clusters.UpdateOneAsync(tx.Session, PostgresFilters.byId current.TenantId current.ClusterId,
                                                    Builders<PostgresCluster>.Update.Inc((fun item -> item.PlanningFence), 1L), cancellationToken = token)
                                return tx.Commit(true)
                }), ct)


    // The scheduled provider sweep is an intentional system-wide view; every operation retains the row's tenant.
    member _.LoadRecoveryPageAsync(after, ct: CancellationToken) =
        task {
            let! page = claims.Find(PostgresDatabaseFilters.recoveryCandidates after).SortBy(fun claim -> claim.Id).Limit(100).ToListAsync(ct)
            return page.ToArray()
        }

    member _.RecordAuditAsync(claim: PostgresManagedDatabase, ct: CancellationToken) =
        task {
            let! _ = claims.UpdateOneAsync(PostgresDatabaseFilters.atRevision claim,
                            Builders<PostgresManagedDatabase>.Update.Set((fun item -> item.LastAuditAtUtc), Some(clock.GetUtcNow().UtcDateTime)), cancellationToken = ct)
            return ()
        }
