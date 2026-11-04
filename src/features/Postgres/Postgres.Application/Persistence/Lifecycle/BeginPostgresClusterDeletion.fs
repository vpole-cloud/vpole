namespace Pole.Features.Postgres.Application.Persistence.Lifecycle

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.ResourceConnections.Contracts
open Pole.Features.Secrets.Contracts

type PostgresClusterDeletionConflict =
    { ClusterId : string
      Reason : PostgresMutationConflictReason }

type BeginPostgresClusterDeletionRequest =
    { Current : PostgresCluster
      Replacement : PostgresCluster
      Authority : GitLifecycleAuthority
      ReviewedConnections : System.Collections.Generic.IReadOnlyList<ResourceConnectionPlanningVersion> }

type BeginPostgresClusterDeletion =
    delegate of request : BeginPostgresClusterDeletionRequest * session : IClientSessionHandle *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterDeletionConflict>>

type MongoBeginPostgresClusterDeletion(
    clusters : IMongoCollection<PostgresCluster>,
    fenceRegisteredResource : FenceRegisteredResource,
    cascadeConnections : CascadeConnectionsForResourceDeletion,
    git : IGitOpsMutations,
    ensureBindingsRemovable : EnsureConsumerSecretBindingRemoval) =
    let revisionConflict clusterId : PostgresOutcome<unit, PostgresClusterDeletionConflict> =
        PostgresError { ClusterId = clusterId; Reason = PostgresMutationConflictReason.ExpectedRevisionConflict }

    member _.InvokeAsync(
        request : BeginPostgresClusterDeletionRequest, session, cancellationToken : CancellationToken) =
        task {
            let current = request.Current
            let replacement = request.Replacement
            PostgresPersistenceGuards.transaction session "PostgreSQL deletion start"
            PostgresPersistenceGuards.deletionTransition current replacement
            let! authorized =
                if not (isNull request.Authority) && isNull request.ReviewedConnections then
                    Task.FromResult false
                else
                    git.AuthorizeAsync(
                        AuthorizeGitResourceMutationRequest(current.TenantId, "PostgresCluster", current.Id,
                            Lifecycle = request.Authority,
                            LifecycleEffect = if isNull request.Authority then Nullable()
                                              else Nullable(GitLifecycleMutationEffect.Remove)),
                        session, cancellationToken)
            if not authorized then
                return PostgresError
                    { ClusterId = current.Id; Reason = PostgresMutationConflictReason.GitOwnedConfiguration }
            else
                let! bindings = ensureBindingsRemovable.Invoke(
                    EnsureConsumerSecretBindingRemovalRequest(current.TenantId,
                        PostgresResourceDeployment.ResourceType, current.Id), session, cancellationToken)
                if bindings.Value :? ConsumerSecretBindingsRequireRemoval then
                    return PostgresError
                        { ClusterId = current.Id; Reason = PostgresMutationConflictReason.SecretBindingsRequireRemoval }
                elif bindings.Value :? ConsumerSecretBindingDeletionChanged then
                    return revisionConflict current.Id
                else
                    let! registration =
                        fenceRegisteredResource.Invoke(
                            FenceRegisteredResourceRequest(
                                current.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                current.Id),
                            session,
                            cancellationToken)
                    if isNull (box registration) then
                        return revisionConflict current.Id
                    else
                        let! cascade =
                            cascadeConnections.Invoke(
                                CascadeConnectionsForResourceDeletionRequest(
                                    current.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    current.Id, request.ReviewedConnections),
                                session,
                                cancellationToken)
                        let onCascaded =
                            Func<ResourceConnectionsCascaded,
                                Task<PostgresOutcome<unit, PostgresClusterDeletionConflict>>>(
                                fun _ -> task {
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
                                            revisionConflict current.Id
                                })
                        let onConflict =
                            Func<ResourceConnectionCascadeConflict,
                                    Task<PostgresOutcome<unit, PostgresClusterDeletionConflict>>>(
                                fun _ -> Task.FromResult(revisionConflict current.Id))
                        return! CascadeConnectionsForResourceDeletionOutcomeExtensions.Match(
                                cascade, onCascaded, onConflict)
        }
