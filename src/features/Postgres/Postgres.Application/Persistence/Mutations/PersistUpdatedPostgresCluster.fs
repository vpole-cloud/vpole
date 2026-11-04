namespace Pole.Features.Postgres.Application.Persistence.Mutations

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.GitOps.Contracts
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceGroups.Contracts

type PostgresMutationConflictReason =
    | ExpectedRevisionConflict = 0
    | GitOwnedConfiguration = 1
    | SecretBindingsRequireRemoval = 2

type PostgresClusterUpdateConflict =
    { ClusterId : string
      Reason : PostgresMutationConflictReason }

type PersistUpdatedPostgresClusterRequest =
    { Current : PostgresCluster
      Replacement : PostgresCluster
      Mutation : GitMutationContext option }

type PersistUpdatedPostgresCluster =
    delegate of request : PersistUpdatedPostgresClusterRequest * session : IClientSessionHandle *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<unit, PostgresClusterUpdateConflict>>

type MongoPersistUpdatedPostgresCluster(
    clusters : IMongoCollection<PostgresCluster>,
    storeOutboxWrites : StoreOutboxWrites,
    git : IGitOpsMutations,
    fenceRegistration : FenceRegisteredResource) =

    member _.InvokeAsync(
        request : PersistUpdatedPostgresClusterRequest, session, cancellationToken : CancellationToken) =
        task {
            let current = request.Current
            let replacement = request.Replacement
            PostgresPersistenceGuards.transaction session "PostgreSQL update"
            PostgresPersistenceGuards.activeUpdate current replacement
            let! authorized = git.AuthorizeAsync(
                AuthorizeGitResourceMutationRequest(current.TenantId, "PostgresCluster", current.Id,
                    request.Mutation |> Option.toObj), session, cancellationToken)

            if not authorized then
                return PostgresError
                    { ClusterId = current.Id
                      Reason = PostgresMutationConflictReason.GitOwnedConfiguration }
            else
                let! registration = fenceRegistration.Invoke(
                    FenceRegisteredResourceRequest(current.TenantId, PostgresResourceDeployment.ResourceType,
                        current.Id), session, cancellationToken)
                if isNull registration then
                    return PostgresError
                        { ClusterId = current.Id
                          Reason = PostgresMutationConflictReason.ExpectedRevisionConflict }
                else
                    let! persisted = clusters.ReplaceOneAsync(session, PostgresFilters.activeAtRevision current,
                        replacement, cancellationToken = cancellationToken)
                    if persisted.ModifiedCount <> 1L then
                        return PostgresError
                            { ClusterId = current.Id
                              Reason = PostgresMutationConflictReason.ExpectedRevisionConflict }
                    else
                        let writes = [| OutboxWrite.Create(PostgresClusterEvents.updated current replacement) |]
                        do! storeOutboxWrites.Invoke(session, writes, cancellationToken)
                        return PostgresSuccess ()
        }
