namespace Pole.Features.Postgres.Application.Persistence.Lifecycle

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.ResourceConnections.Contracts

type PostgresClusterDeletionConflict =
    { ClusterId : string }

type BeginPostgresClusterDeletion =
    delegate of session : IClientSessionHandle * current : PostgresCluster * replacement : PostgresCluster * cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterDeletionConflict>>

type MongoBeginPostgresClusterDeletion(
    clusters : IMongoCollection<PostgresCluster>,
    fenceRegisteredResource : FenceRegisteredResource,
    cascadeConnections : CascadeConnectionsForResourceDeletion) =
    member _.InvokeAsync(session, current, replacement, cancellationToken : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL deletion start"
            PostgresPersistenceGuards.deletionTransition current replacement
            let! registration =
                fenceRegisteredResource.Invoke(
                    current.TenantId, PostgresResourceDeployment.ResourceType, current.Id, session, cancellationToken)
            if isNull (box registration) then
                return PostgresError { ClusterId = current.Id }
            else
                let! cascade =
                    cascadeConnections.Invoke(
                        current.TenantId, PostgresResourceDeployment.ResourceType, current.Id, session, cancellationToken)
                match cascade.Value with
                | :? ResourceConnectionCascadeConflict ->
                    return PostgresError { ClusterId = current.Id }
                | _ ->
                    let! persisted =
                        clusters.ReplaceOneAsync(
                            session,
                            PostgresFilters.activeAtRevision current,
                            replacement,
                            cancellationToken = cancellationToken)
                    return
                        if persisted.ModifiedCount = 1L then
                            PostgresSuccess ()
                        else
                            PostgresError { ClusterId = current.Id }
        }
