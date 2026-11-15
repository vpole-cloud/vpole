namespace Pole.Features.Postgres.Application.Jobs

open System
open System.Threading
open System.Threading.Tasks
open Hangfire
open Microsoft.Extensions.Logging
open Pole.Features.Postgres.Application.ManagedDatabases
open Pole.Features.Postgres.Contracts

type PostgresManagedDatabaseRecoveryJob
    (store: PostgresManagedDatabaseStateStore, ensure: EnsurePostgresManagedDatabase,
     purge: PurgePostgresManagedDatabase, clock: TimeProvider, logger: ILogger<PostgresManagedDatabaseRecoveryJob>) =
    [<DisableConcurrentExecution(600)>]
    member _.RecoverAsync(ct: CancellationToken) : Task =
        task {
            let mutable after = None
            let mutable more = true
            let mutable failed = false
            while more do
                let! page = store.LoadRecoveryPageAsync(after, ct)
                for claim in page do
                    ct.ThrowIfCancellationRequested()
                    try
                        let owner = PostgresManagedDatabaseOwner(claim.Owner.ResourceType, claim.Owner.ResourceId)
                        if claim.IsActive then
                            let! _ = ensure.Invoke(EnsurePostgresManagedDatabaseRequest(claim.TenantId, owner), ct)
                            ()
                        elif not claim.IsPurged ||
                             (claim.LastAuditAtUtc |> Option.forall(fun last -> last <= clock.GetUtcNow().UtcDateTime.AddMinutes(-30.))) then
                            let! result = purge.Invoke(PurgePostgresManagedDatabaseRequest(claim.TenantId, owner), ct)
                            if result.Value :? PostgresManagedDatabasePurged then do! store.RecordAuditAsync(claim, ct)
                    with error when not ct.IsCancellationRequested ->
                        // Kubernetes/provider exceptions may contain response bodies; log only stable owned identity.
                        logger.LogWarning("Managed PostgreSQL database recovery failed for claim {ClaimId} ({FailureType}).", claim.Id, error.GetType().Name)
                        failed <- true
                more <- page.Length = 100
                if page.Length > 0 then after <- Some page[page.Length - 1].Id
            if failed then invalidOp "One or more managed PostgreSQL database recoveries failed."
        } :> Task
