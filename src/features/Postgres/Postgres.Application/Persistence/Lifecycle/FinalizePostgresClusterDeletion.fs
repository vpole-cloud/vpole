namespace Pole.Features.Postgres.Application.Persistence.Lifecycle

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.ResultType

type PostgresClusterDeletionFinalizationError =
    | PostgresClusterDeletionFinalizationConflict of clusterId : string
    | PostgresClusterDeletionDeploymentPurgeConflict of ResourceDeploymentFinalizationConflict
    | PostgresClusterDeletionRegistrationConflict of ResourceRegistrationMutationError

type FinalizePostgresClusterDeletion =
    delegate of tenantId : string * clusterId : string * cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>

type MongoFinalizePostgresClusterDeletion(
    clusters : IMongoCollection<PostgresCluster>,
    credentials : IMongoCollection<PostgresCredential>,
    getDeploymentReadiness : GetResourceDeploymentFinalizationReadiness,
    purgeDeployments : PurgeDeletedResourceDeployments,
    unregisterResource : UnregisterResource,
    runMongoTransaction : RunMongoOutcomeTransaction<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>,
    storeOutboxWrites : StoreOutboxWrites) =

    member _.InvokeAsync(tenantId, clusterId, cancellationToken : CancellationToken) =
        runMongoTransaction.Invoke(
            Func<
                MongoTransactionContext<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>,
                CancellationToken,
                Task<MongoTransactionDecision<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>>>(
                fun transaction transactionCancellationToken ->
                    task {
                        PostgresPersistenceGuards.identity tenantId clusterId
                        let clusterFilter = PostgresFilters.deletingById tenantId clusterId
                        let! cluster =
                            clusters
                                .Find(transaction.Session, clusterFilter)
                                .FirstOrDefaultAsync(transactionCancellationToken)
                        if isNull (box cluster) then
                            return transaction.Commit(PostgresSuccess ())
                        else
                            let! readiness =
                                getDeploymentReadiness.Invoke(
                                    tenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    clusterId,
                                    transaction.Session,
                                    transactionCancellationToken)
                            if readiness <> ResourceDeploymentFinalizationReadiness.Ready then
                                return transaction.Commit(PostgresSuccess ())
                            else
                                let! deleted =
                                    clusters.DeleteOneAsync(
                                        transaction.Session,
                                        PostgresFilters.deletingAtRevision cluster,
                                        cancellationToken = transactionCancellationToken)
                                if deleted.DeletedCount <> 1L then
                                    return
                                        transaction.Abort(
                                            PostgresError(
                                                PostgresClusterDeletionFinalizationConflict clusterId))
                                else
                                    let! registrationResult =
                                        unregisterResource.Invoke(
                                            tenantId,
                                            cluster.GroupId,
                                            PostgresResourceDeployment.ResourceType,
                                            clusterId,
                                            transaction.Session,
                                            transactionCancellationToken)

                                    match TypedResultInterop.toResult registrationResult with
                                    | Error error ->
                                        return
                                            transaction.Abort(
                                                PostgresError(
                                                    PostgresClusterDeletionRegistrationConflict error))
                                    | Ok _ ->
                                        let! _ =
                                            credentials.DeleteOneAsync(
                                                transaction.Session,
                                                PostgresFilters.credentialForDeletedCluster tenantId clusterId,
                                                cancellationToken = transactionCancellationToken)
                                        let! purged =
                                            purgeDeployments.Invoke(
                                                tenantId,
                                                PostgresResourceDeployment.ResourceType,
                                                clusterId,
                                                transaction.Session,
                                                transactionCancellationToken)
                                        let mutable ignored = Unchecked.defaultof<Unit>
                                        let mutable purgeConflict = Unchecked.defaultof<ResourceDeploymentFinalizationConflict>
                                        if TypedResultExtensions.TryGetValue(
                                            purged,
                                            &ignored,
                                            &purgeConflict) then
                                            let writes : IReadOnlyCollection<OutboxWrite> =
                                                [| OutboxWrite.Create(PostgresClusterEvents.deleted cluster) |]
                                            do! storeOutboxWrites.Invoke(
                                                transaction.Session,
                                                writes,
                                                transactionCancellationToken)
                                            return transaction.Commit(PostgresSuccess ())
                                        else
                                            return
                                                transaction.Abort(
                                                    PostgresError(
                                                        PostgresClusterDeletionDeploymentPurgeConflict purgeConflict))
                    }),
            cancellationToken)
