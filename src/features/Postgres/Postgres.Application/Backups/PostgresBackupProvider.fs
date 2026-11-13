namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open Pole.Features.Backups.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Endpoints
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
            let request =
                { TenantId = r.Archive.Source.TenantId
                  Body = { Name = r.Name; GroupId = p.ResourceGroupId; Version = version
                           Instances = Some r.Archive.Service.Instances; Storage = Some(r.StorageGiB.GetValueOrDefault(r.Archive.Service.StorageGiB))
                           StorageProfileId = p.StorageProfileId; ComputeProfileId = Some p.ComputeProfileId
                           Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                           Placement = Some(PlacementRequest(p.IsolationScopeId,p.DeploymentTargetId)) } }
            let! result = creator.CreateAsync(request,r.NewResourceId,origin,Some p.RuntimeCellId,Some recordTarget,ct)
            match result with
            | PostgresClusterCreated _ -> return running
            | CreatePostgresClusterRejected(CreateNameAlreadyExists _)
            | CreatePostgresClusterRejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                // A lease may expire while a second worker is completing the transaction.
                let! winner = load.Invoke(request.TenantId,r.NewResourceId,ct)
                match winner with
                | Some c when not (isNull c.Spec.Recovery) && c.Spec.Recovery.OperationId = r.OperationId -> return running
                | _ ->
                    match result with
                    | CreatePostgresClusterRejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                        return BackupNative.fail "restore-resource-deleted" false
                    | _ -> return BackupNative.fail "restore-name-already-exists" false
            | CreatePostgresClusterRejected error ->
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

    let restoredStatus (client: IKubernetes) (r: RestoreBackupRequest) (cluster: PostgresCluster) ct =
        task {
            let archive = r.Archive
            let p = archive.Placement
            let origin = cluster.Spec.Recovery
            if isNull origin || origin.OperationId <> r.OperationId || origin.ArchiveId <> archive.ArchiveId
               || origin.ServerName <> archive.ServerName || origin.BackupId <> r.BackupId
               || origin.TargetTimeUtc <> (if r.TargetTime.HasValue then Nullable(r.TargetTime.Value.UtcDateTime) else Nullable()) then
                BackupNative.fail "restore-identity-conflict" false
            let! target = placement.Invoke(LoadResourceDeploymentRequest(cluster.TenantId,resourceType,cluster.Id),ct)
            if isNull target || target.RuntimeCellId <> p.RuntimeCellId || target.IsolationScopeId <> p.IsolationScopeId
               || target.DeploymentTargetId <> p.DeploymentTargetId then BackupNative.fail "restore-placement-changed" false
            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let! native = BackupNative.get client PostgresBackupManifests.group p.NamespaceName "clusters" name ct
            if isNull native then return running
            else
                if not (BackupNative.labelsMatch native
                            ["pole.io/tenant-id",cluster.TenantId; "pole.io/resource-id",cluster.Id
                             "pole.io/runtime-cell-id",target.RuntimeCellId; "pole.io/resource-deployment-id",target.DeploymentId]) then
                    BackupNative.fail "restore-identity-conflict" false
                let recovery = BackupNative.field native ["spec";"bootstrap";"recovery"]
                let targetMatches =
                    if r.TargetTime.HasValue then
                        BackupNative.timestamp recovery ["recoveryTarget";"targetTime"] = r.TargetTime
                    else
                        BackupNative.value recovery ["recoveryTarget";"backupID"] = r.BackupId
                        && String.Equals(BackupNative.value recovery ["recoveryTarget";"targetImmediate"],"true",StringComparison.OrdinalIgnoreCase)
                if not targetMatches || BackupNative.value recovery ["source"] <> "origin" then
                    BackupNative.fail "restore-target-mismatch" false
                let ready = BackupNative.condition native "Ready"
                let primary = BackupNative.value native ["status";"currentPrimary"]
                if target.Status = ResourceDeploymentStatus.Applied && target.DesiredGeneration = target.ObservedGeneration
                   && not (String.IsNullOrEmpty primary) && BackupNative.value ready ["status"] = "True" then
                    return BackupProviderProgress(BackupProviderPhase.Succeeded,r.BackupId)
                else
                    let! jobs = client.BatchV1.ListNamespacedJobAsync(p.NamespaceName,labelSelector=("cnpg.io/cluster=" + name + ",cnpg.io/jobRole=full-recovery"),cancellationToken=ct)
                    let failed =
                        jobs.Items |> Seq.exists (fun job ->
                            not (isNull job.Metadata.OwnerReferences)
                            && (job.Metadata.OwnerReferences |> Seq.exists (fun owner -> owner.Uid = BackupNative.uid native && owner.Kind = "Cluster"))
                            && not (isNull job.Status)
                            && not (isNull job.Status.Conditions)
                            && (job.Status.Conditions |> Seq.exists (fun c -> (c.Type = "Failed" || c.Type = "FailureTarget") && c.Status = "True")))
                    if failed then return BackupNative.fail "restore-recovery-failed" false
                    else return running
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
                    if isNull request.Schedule then clients.GetForCleanupAsync(request.Archive.Placement.DeploymentTargetId,ct)
                    else clientFor request.Archive ct
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
                let! existing = load.Invoke(archive.Source.TenantId,r.NewResourceId,ct)
                match existing with
                | Some cluster -> return! restoredStatus client r cluster ct
                | None ->
                    if r.TargetTime.HasValue && r.TargetTime.Value.UtcTicks % 10L <> 0L then
                        BackupNative.fail "restore-target-time-precision-unsupported" false
                    if r.StorageGiB.HasValue && r.StorageGiB.Value < archive.Service.StorageGiB then
                        BackupNative.fail "restore-storage-too-small" false
                    let valid,version = Int32.TryParse(archive.Service.Version)
                    if not valid || not (PostgresSettings.isSupportedVersion settings version) then
                        BackupNative.fail "restore-version-unsupported" false
                    let! _ = BackupNative.ensureStore client archive ct
                    let time = if r.TargetTime.HasValue then Nullable(r.TargetTime.Value.UtcDateTime) else Nullable()
                    let origin = PostgresRecoveryOrigin(r.OperationId,archive.ArchiveId,archive.ServerName,r.BackupId,time)
                    return! createRestored r origin version ct }) ct
