namespace Pole.Features.Postgres.Application.Lifecycle

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Features.Postgres.Application.Credentials

type PostgresClusterDeletionFinalizationError =
    | PostgresClusterDeletionFinalizationConflict of clusterId: string
    | PostgresClusterDeletionDeploymentPurgeConflict of ResourceDeploymentFinalizationConflict
    | PostgresClusterDeletionRegistrationConflict of ResourceRegistrationMutationError
    | PostgresClusterDeletionSecretConflict of PostgresCredentialError

type FinalizePostgresClusterDeletion =
    delegate of
        tenantId: string * clusterId: string * cancellationToken: CancellationToken ->
            Task<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>

type MongoFinalizePostgresClusterDeletion
    (
        clusters: IMongoCollection<PostgresCluster>,
        releaseSecret: ReleaseManagedSecret,
        getDeploymentReadiness: GetResourceDeploymentFinalizationReadiness,
        purgeDeployments: PurgeDeletedResourceDeployments,
        resourceRegistrar: IResourceRegistrar,
        runMongoTransaction: RunMongoOutcomeTransaction<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>,
        storeOutboxWrites: StoreOutboxWrites
    ) =

    member _.InvokeAsync(tenantId, clusterId, cancellationToken: CancellationToken) =
        runMongoTransaction.Invoke(
            Func<
                MongoTransactionContext<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>,
                CancellationToken,
                Task<MongoTransactionDecision<PostgresOutcome<unit, PostgresClusterDeletionFinalizationError>>>
             >
                (fun transaction transactionCancellationToken ->
                    task {
                        PostgresPersistenceGuards.identity tenantId clusterId
                        let clusterFilter = PostgresFilters.deletingById tenantId clusterId

                        let! cluster =
                            clusters
                                .Find(transaction.Session, clusterFilter)
                                .FirstOrDefaultAsync(transactionCancellationToken)

                        if isNull (box cluster) then
                            return transaction.Commit(PostgresSuccess())
                        else
                            let! readiness =
                                getDeploymentReadiness.Invoke(
                                    GetResourceDeploymentFinalizationReadinessRequest(
                                        tenantId,
                                        PostgresResourceDeployment.ResourceType,
                                        clusterId
                                    ),
                                    transaction.Session,
                                    transactionCancellationToken
                                )

                            if readiness <> ResourceDeploymentFinalizationReadiness.Ready then
                                return transaction.Commit(PostgresSuccess())
                            else
                                let! released =
                                    releaseSecret.Invoke(
                                        ReleaseManagedSecretRequest(tenantId, PostgresManagedSecret.owner clusterId),
                                        transaction.Session,
                                        transactionCancellationToken
                                    )

                                match released.Value with
                                | :? ManagedSecretFailure as error ->
                                    return
                                        transaction.Abort(
                                            PostgresError(
                                                PostgresClusterDeletionSecretConflict(
                                                    PostgresCredentialErrors.fromManaged clusterId error
                                                )
                                            )
                                        )
                                | :? ManagedSecretMutationSucceeded ->
                                    let! deleted =
                                        clusters.DeleteOneAsync(
                                            transaction.Session,
                                            PostgresFilters.deletingAtRevision cluster,
                                            cancellationToken = transactionCancellationToken
                                        )

                                    if deleted.DeletedCount <> 1L then
                                        return
                                            transaction.Abort(
                                                PostgresError(PostgresClusterDeletionFinalizationConflict clusterId)
                                            )
                                    else
                                        let! registrationResult =
                                            resourceRegistrar.UnregisterAsync(
                                                UnregisterResourceRequest(
                                                    tenantId,
                                                    cluster.GroupId,
                                                    PostgresResourceDeployment.ResourceType,
                                                    clusterId
                                                ),
                                                transaction.Session,
                                                transactionCancellationToken
                                            )

                                        match TypedResultInterop.resourceUnregistration registrationResult with
                                        | Error error ->
                                            return
                                                transaction.Abort(
                                                    PostgresError(PostgresClusterDeletionRegistrationConflict error)
                                                )
                                        | Ok _ ->
                                            let! purged =
                                                purgeDeployments.Invoke(
                                                    PurgeDeletedResourceDeploymentsRequest(
                                                        tenantId,
                                                        PostgresResourceDeployment.ResourceType,
                                                        clusterId
                                                    ),
                                                    transaction.Session,
                                                    transactionCancellationToken
                                                )

                                            match purged.Value with
                                            | :? ResourceDeploymentsPurged ->
                                                let writes: IReadOnlyCollection<OutboxWrite> =
                                                    [| OutboxWrite.Create(PostgresClusterEvents.deleted cluster) |]

                                                do!
                                                    storeOutboxWrites.Invoke(
                                                        transaction.Session,
                                                        writes,
                                                        transactionCancellationToken
                                                    )

                                                return transaction.Commit(PostgresSuccess())
                                            | :? ResourceDeploymentFinalizationConflict as purgeConflict ->
                                                return
                                                    transaction.Abort(
                                                        PostgresError(
                                                            PostgresClusterDeletionDeploymentPurgeConflict
                                                                purgeConflict
                                                        )
                                                    )
                                            | value ->
                                                return
                                                    invalidOp $"Unexpected resource deployment purge outcome: {value}"
                                | _ -> return invalidOp "Unexpected managed Secret release outcome."
                    }),
            cancellationToken
        )
