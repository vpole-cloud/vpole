namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open Pole.Features.Backups.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Creation
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

module private ProviderResult =
    let protect (work: unit -> Task<'t>) (ct: CancellationToken) =
        task {
            try
                let! value = work()
                return BackupProviderResult<'t>.Success(value) :> BackupProviderResult<'t>
            with
            | PostgresBackupFailure(code,retry) -> return BackupProviderResult<'t>.Failure(code,retry) :> _
            | :? DeploymentTargetUnavailableException -> return BackupProviderResult<'t>.Failure("backup-target-unavailable",true) :> _
            | :? HttpOperationException as e ->
                let denied = e.Response.StatusCode = HttpStatusCode.Forbidden || e.Response.StatusCode = HttpStatusCode.Unauthorized
                let code = if denied then "backup-kubernetes-access-denied" else "backup-kubernetes-unavailable"
                return BackupProviderResult<'t>.Failure(code,true) :> _
            | :? HttpRequestException -> return BackupProviderResult<'t>.Failure("backup-target-unavailable",true) :> _
            | :? OperationCanceledException when not ct.IsCancellationRequested ->
                return BackupProviderResult<'t>.Failure("backup-target-timeout",true) :> _
        }

type PostgresBackupProvider
    (clients: IKubernetesClientProvider, load: LoadPostgresClusterById, placement: LoadResourceDeployment,
     creator: PostgresClusterCreator, settings: PostgresSettings, recordTarget: RecordRestoreTargetCreation) =

    let resourceType = PostgresResourceDeployment.ResourceType
    let running = BackupProviderProgress(BackupProviderPhase.Running,null)
    let clientFor (archive: BackupArchiveContext) ct = clients.GetAsync(archive.Placement.DeploymentTargetId,ct)

    let createRestored (r: RestoreBackupRequest) origin version ct =
        task {
            let p = r.Archive.Placement
            let configuration =
                { Name = r.Resource.Name
                  GroupId = p.ResourceGroupId
                  Version = version
                  Instances = Some r.Archive.Service.Instances
                  Storage = Some(r.Resource.StorageGiB.GetValueOrDefault(r.Archive.Service.StorageGiB))
                  StorageProfileId = p.StorageProfileId
                  ComputeProfileId = Some p.ComputeProfileId
                  Placement = Some(PlacementRequest(p.IsolationScopeId, p.DeploymentTargetId)) }
            let request =
                { TenantId = r.Archive.Source.TenantId
                  ResourceId = r.Resource.Id
                  Configuration = configuration
                  Authority =
                    PostgresCreationAuthority.Recovery(
                        origin,
                        { RuntimeCellId = p.RuntimeCellId },
                        recordTarget
                    ) }
            let! result = creator.CreateAsync(request, ct)
            match result with
            | PostgresCreationOutcome.Created _ -> return running
            | PostgresCreationOutcome.Rejected(CreateNameAlreadyExists _)
            | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                // A lease may expire while a second worker is completing the transaction.
                let! winner = load.Invoke(request.TenantId,r.Resource.Id,ct)
                match winner with
                | Some cluster when
                    match cluster.Spec.Bootstrap with
                    | :? RecoveryPostgresBootstrap as recovery -> recovery.Origin.OperationId = r.OperationId
                    | _ -> false
                    -> return running
                | _ ->
                    match result with
                    | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                        return BackupNative.fail "restore-resource-deleted" false
                    | _ -> return BackupNative.fail "restore-name-already-exists" false
            | PostgresCreationOutcome.Rejected error ->
                let code =
                    match error with
                    | CreateQuota _ -> "restore-quota-rejected"
                    | CreateTarget _ -> "restore-target-rejected"
                    | CreateCompute _ -> "restore-compute-rejected"
                    | CreateStorage _ -> "restore-storage-rejected"
                    | CreateCredential _ -> "restore-credential-rejected"
                    | CreateState PostgresClusterRestoreOperationUnavailable -> "restore-operation-unavailable"
                    | _ -> "restore-placement-rejected"
                return BackupNative.fail code false
        }

    let restoredStatus (client: IKubernetes) (request: RestoreBackupRequest) (cluster: PostgresCluster) ct =
        task {
            let archive = request.Archive
            let sourcePlacement = archive.Placement
            let originMatches =
                match cluster.Spec.Bootstrap with
                | :? RecoveryPostgresBootstrap as recovery ->
                    let origin = recovery.Origin
                    let targetMatches =
                        match origin.Target, request.Target with
                        | (:? PostgresCopyRecoveryTarget as copy), (:? BackupCopyRecoveryTarget as selected) ->
                            copy.BackupId = selected.BackupId
                        | (:? PostgresTimeRecoveryTarget as time), (:? BackupTimeRecoveryTarget as selected) ->
                            time.TargetTimeUtc = selected.TargetTime.UtcDateTime
                        | _ -> false
                    origin.OperationId = request.OperationId
                    && origin.ArchiveId = archive.Location.ArchiveId
                    && origin.ServerName = archive.Location.ServerName
                    && targetMatches
                | _ -> false

            if not originMatches then
                BackupNative.fail "restore-identity-conflict" false

            let! target =
                placement.Invoke(LoadResourceDeploymentRequest(cluster.TenantId, resourceType, cluster.Id), ct)
            if isNull target || target.RuntimeCellId <> sourcePlacement.RuntimeCellId
               || target.IsolationScopeId <> sourcePlacement.IsolationScopeId
               || target.DeploymentTargetId <> sourcePlacement.DeploymentTargetId then
                BackupNative.fail "restore-placement-changed" false

            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let! native =
                BackupNative.get client PostgresBackupNames.Group sourcePlacement.NamespaceName "clusters" name ct
            if isNull native then
                return running
            else
                let labels =
                    [ "pole.io/tenant-id", cluster.TenantId
                      "pole.io/resource-id", cluster.Id
                      "pole.io/runtime-cell-id", target.RuntimeCellId
                      "pole.io/resource-deployment-id", target.DeploymentId ]
                if not (BackupNative.labelsMatch native labels) then
                    BackupNative.fail "restore-identity-conflict" false

                let nativeRecovery = BackupNative.field native ["spec"; "bootstrap"; "recovery"]
                let targetMatches =
                    match request.Target with
                    | :? BackupTimeRecoveryTarget as time ->
                        let actualTime = BackupNative.timestamp nativeRecovery ["recoveryTarget"; "targetTime"]
                        actualTime = Nullable(time.TargetTime.ToUniversalTime())
                    | :? BackupCopyRecoveryTarget as copy ->
                        BackupNative.value nativeRecovery ["recoveryTarget"; "backupID"] = copy.BackupId
                        && String.Equals(
                            BackupNative.value nativeRecovery ["recoveryTarget"; "targetImmediate"],
                            "true", StringComparison.OrdinalIgnoreCase)
                    | _ -> invalidOp "Unsupported backup recovery target."
                if not targetMatches || BackupNative.value nativeRecovery ["source"] <> "origin" then
                    BackupNative.fail "restore-target-mismatch" false

                let ready = BackupNative.condition native "Ready"
                let primary = BackupNative.value native ["status"; "currentPrimary"]
                if target.Status = ResourceDeploymentStatus.Applied
                   && target.DesiredGeneration = target.ObservedGeneration
                   && not (String.IsNullOrEmpty primary)
                   && BackupNative.value ready ["status"] = "True" then
                    let backupId =
                        match request.Target with
                        | :? BackupCopyRecoveryTarget as copy -> copy.BackupId
                        | :? BackupTimeRecoveryTarget -> null
                        | _ -> invalidOp "Unsupported backup recovery target."
                    return BackupProviderProgress(BackupProviderPhase.Succeeded, backupId)
                else
                    let! jobs =
                        client.BatchV1.ListNamespacedJobAsync(
                            sourcePlacement.NamespaceName,
                            labelSelector = ("cnpg.io/cluster=" + name + ",cnpg.io/jobRole=full-recovery"),
                            cancellationToken = ct)
                    let failed =
                        jobs.Items |> Seq.exists (fun job ->
                            not (isNull job.Metadata.OwnerReferences)
                            && (job.Metadata.OwnerReferences |> Seq.exists (fun owner ->
                                owner.Uid = BackupNative.uid native && owner.Kind = "Cluster"))
                            && not (isNull job.Status)
                            && not (isNull job.Status.Conditions)
                            && (job.Status.Conditions |> Seq.exists (fun condition ->
                                (condition.Type = "Failed" || condition.Type = "FailureTarget")
                                && condition.Status = "True")))
                    if failed then
                        return BackupNative.fail "restore-recovery-failed" false
                    else
                        return running
        }

    interface IBackupProvider with
        member _.ResourceType = resourceType.Value
        member _.DeclarationKind = "PostgresCluster"
        member _.SupportsPointInTimeRecovery = true
        member _.ValidateSchedule(schedule) =
            if schedule.RetentionDays > 0 then ValidateBackupScheduleOutcome(BackupScheduleAccepted())
            else ValidateBackupScheduleOutcome(BackupRetentionInvalid())

        member _.DescribeAsync(source, ct) =
            task {
                if source.ResourceType <> resourceType.Value then
                    return DescribeBackupSourceOutcome(BackupSourceUnsupported())
                else
                    let! current = load.Invoke(source.TenantId, source.ResourceId, ct)
                    match current with
                    | Some cluster when cluster.Lifecycle :? ActivePostgresClusterLifecycle && not cluster.IsPrepared ->
                        return DescribeBackupSourceOutcome(
                            BackupServiceSnapshot(string cluster.Spec.Version, cluster.Spec.Storage,
                                cluster.Spec.Instances, cluster.Revision))
                    | Some _
                    | None -> return DescribeBackupSourceOutcome(BackupSourceUnavailable())
            }

        member _.ListAsync(request,ct) = BarmanCatalog.read request ct

        member _.ApplyScheduleAsync(request,ct) =
            ProviderResult.protect (fun () -> task {
                let! client =
                    match request.Configuration with
                    | :? StopBackupPolicy -> clients.GetForCleanupAsync(request.Archive.Placement.DeploymentTargetId, ct)
                    | :? ConfigureBackupPolicy -> clientFor request.Archive ct
                    | _ -> invalidOp "Unsupported backup policy configuration."
                return! BackupNative.apply client request ct }) ct

        member _.ObserveAsync(archive,ct) =
            ProviderResult.protect (fun () -> task {
                let! client = clientFor archive ct
                return! BackupNative.observe client archive ct }) ct

        member _.RunAsync(request,ct) =
            ProviderResult.protect (fun () -> task {
                let! client = clientFor request.Archive ct
                return! BackupNative.run client request ct }) ct

        member _.RestoreAsync(r,ct) =
            ProviderResult.protect (fun () -> task {
                let archive = r.Archive
                let! client = clientFor archive ct
                let! existing = load.Invoke(archive.Source.TenantId,r.Resource.Id,ct)
                match existing with
                | Some cluster -> return! restoredStatus client r cluster ct
                | None ->
                    let unsupportedPrecision =
                        match r.Target with
                        | :? BackupTimeRecoveryTarget as time -> time.TargetTime.UtcTicks % 10L <> 0L
                        | :? BackupCopyRecoveryTarget -> false
                        | _ -> invalidOp "Unsupported backup recovery target."
                    if unsupportedPrecision then
                        BackupNative.fail "restore-target-time-precision-unsupported" false
                    if r.Resource.StorageGiB.HasValue && r.Resource.StorageGiB.Value < archive.Service.StorageGiB then
                        BackupNative.fail "restore-storage-too-small" false
                    let valid,version = Int32.TryParse(archive.Service.Version)
                    if not valid || not (PostgresSettings.isSupportedVersion settings version) then
                        BackupNative.fail "restore-version-unsupported" false
                    let! _ = BackupNative.ensureStore client archive r.Projection ct
                    let target: PostgresRecoveryTarget =
                        match r.Target with
                        | :? BackupTimeRecoveryTarget as time -> PostgresTimeRecoveryTarget(time.TargetTime.UtcDateTime)
                        | :? BackupCopyRecoveryTarget as copy -> PostgresCopyRecoveryTarget(copy.BackupId)
                        | _ -> invalidOp "Unsupported backup recovery target."
                    let origin =
                        { OperationId = r.OperationId
                          ArchiveId = archive.Location.ArchiveId
                          ServerName = archive.Location.ServerName
                          Target = target }
                    return! createRestored r origin version ct }) ct
