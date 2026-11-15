namespace Pole.Features.Postgres.Application.ManagedDatabases

open System.Threading
open k8s
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

type PurgeOwnedPostgresManagedDatabase
    (store: PostgresManagedDatabaseStateStore, clients: IKubernetesClientProvider,
     native: PostgresManagedDatabaseNative, probe: PostgresManagedDatabaseProbe,
     loadPlacements: LoadResourceDeploymentRuntimePlacements) =

    let pending reason = PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabasePending(reason))

    let authorizeAndBegin (request: PurgePostgresManagedDatabaseRequest) ct =
        task {
            let! current = store.LoadAsync(request.TenantId, request.Owner, ct)
            if isNull (box current) then return Ok None
            else
                let! sources = loadPlacements.Invoke(LoadResourceDeploymentRuntimePlacementsRequest(
                                    current.TenantId, ResourceTypeId.Create(current.Owner.ResourceType), [| current.Owner.ResourceId |]), ct)
                let invalidSource =
                    sources |> Seq.exists(fun item ->
                        item.ResourceId <> current.Owner.ResourceId ||
                        item.DeploymentTargetId <> current.Target.DeploymentTargetId ||
                        item.IsolationScopeId <> current.Source.IsolationScopeId)
                if invalidSource then return Error(PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabaseUnavailable()))
                else
                    let! started = store.BeginPurgeAsync(request.TenantId, request.Owner, ct)
                    return
                        match started with
                        | None -> Error(PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged()))
                        | Some claim -> Ok(Some claim)
        }

    let revokeAccess (client: IKubernetes) (claim: PostgresManagedDatabase) ct =
        task {
            let! ingressGone = native.DeletePolicyAsync(client, claim, PostgresManagedDatabaseManifests.policyName claim, ct)
            let! probeAccessGone = native.DeletePolicyAsync(client, claim, PostgresManagedDatabaseProbeManifests.policyName claim, ct)
            let! probeGone = probe.DeleteAsync(client, claim, ct)
            return ingressGone && probeAccessGone && probeGone
        }

    let removeOwnedDatabase (client: IKubernetes) (claim: PostgresManagedDatabase) ct =
        task {
            let! accessGone = revokeAccess client claim ct
            if not accessGone then return pending PostgresManagedDatabasePendingReason.PurgePending
            else
                let! databaseGone = native.DeleteCustomAsync(client, claim, "databases", ct)
                if not databaseGone then return pending PostgresManagedDatabasePendingReason.PurgePending
                else
                    let! roleGone = native.DeleteCustomAsync(client, claim, "databaseroles", ct)
                    if not roleGone then return pending PostgresManagedDatabasePendingReason.PurgePending
                    else
                        let! completed = store.CompletePurgeAsync(claim, ct)
                        return
                            if completed then PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabasePurged())
                            else PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
        }

    member _.InvokeAsync(request: PurgePostgresManagedDatabaseRequest, ct: CancellationToken) =
        task {
            let! authorized = authorizeAndBegin request ct
            match authorized with
            | Error outcome -> return outcome
            | Ok None -> return PurgePostgresManagedDatabaseOutcome(PostgresManagedDatabasePurged())
            | Ok(Some claim) ->
                try
                    let! client = clients.GetForCleanupAsync(claim.Target.DeploymentTargetId, ct)
                    return! removeOwnedDatabase client claim ct
                with :? DeploymentTargetUnavailableException ->
                    return pending PostgresManagedDatabasePendingReason.PlacementUnavailable
        }
