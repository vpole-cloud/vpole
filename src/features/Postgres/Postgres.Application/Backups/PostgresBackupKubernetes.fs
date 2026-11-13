namespace Pole.Features.Postgres.Application.Backups

open System
open System.Text.Json
open System.Threading.Tasks
open k8s
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

exception internal PostgresBackupFailure of string * bool

module internal BackupNative =
    open PostgresBackupObjects
    open PostgresBackupArchive
    module M = PostgresBackupManifests

    let fail code retryable = raise (PostgresBackupFailure(code, retryable))

    let private nativeIdentity current =
        match identity current with
        | Some identity -> identity
        | None -> fail "backup-native-identity-missing" true

    let uid current = (nativeIdentity current).Uid

    let private ensureArchive current archive =
        if not (ownsArchive current archive) then fail "backup-resource-ownership-conflict" false

    let private source client archive ct =
        task {
            let! loaded = PostgresBackupArchive.source client archive ct
            return
                match loaded with
                | Ok(current, _) -> current
                | Error SourceUnavailable -> fail "backup-source-unavailable" true
                | Error SourceOwnershipConflict -> fail "backup-resource-ownership-conflict" false
                | Error SourceIdentityUnavailable -> fail "backup-native-identity-missing" true
        }

    let private state doc =
        match value doc ["status";"phase"] with
        | "completed" -> BackupObservedRunState.Succeeded
        | "failed" | "invalid backup definition" -> BackupObservedRunState.Failed
        | _ -> BackupObservedRunState.Running

    let private failureCode native =
        let message = value native ["status";"error"] |> Option.ofObj |> Option.defaultValue ""
        let contains (tokens: string list) = tokens |> List.exists (fun token -> message.Contains(token,StringComparison.OrdinalIgnoreCase))
        if contains ["requested plugin is not available";"unknown plugin";"error loading plugin"] then "backup-plugin-unavailable"
        elif contains ["AccessDenied";"InvalidAccessKeyId";"SignatureDoesNotMatch"] then "backup-storage-access-denied"
        elif contains ["connection refused";"no such host";"could not connect to the endpoint"] then "backup-storage-unreachable"
        elif value native ["status";"phase"] = "invalid backup definition" then "backup-definition-invalid"
        else "backup-execution-failed"

    let run client (request: BackupCopyRequest) ct =
        task {
            let a = request.Archive
            let ns = a.Placement.NamespaceName
            let! cluster = source client a ct
            let! current = get client PostgresBackupNames.Group ns "backups" (PostgresBackupNames.manualBackup request.OperationId) ct
            let! native =
                if isNull current then
                    task {
                        if not (active cluster a) then fail "backup-archive-inactive" true
                        let! ready = PostgresBackupArchive.podsReady client a cluster (nativeIdentity cluster) ct
                        if not ready then fail "backup-plugin-not-ready" true
                        return! create client PostgresBackupNames.Group ns "backups" (M.backup request (uid cluster)) ct
                    }
                else
                    ensureArchive current a
                    Task.FromResult(current)
            if value native ["spec";"cluster";"name"] <> M.clusterName a then fail "backup-resource-ownership-conflict" false
            let backupId = value native ["status";"backupId"]
            match state native with
            | BackupObservedRunState.Succeeded when not (String.IsNullOrEmpty backupId) ->
                return BackupProviderProgress(BackupProviderPhase.Succeeded,backupId)
            | BackupObservedRunState.Failed -> return fail (failureCode native) false
            | _ -> return BackupProviderProgress(BackupProviderPhase.Running,null)
        }

    let observe (client: IKubernetes) (archive: BackupArchiveContext) ct =
        task {
            let ns = archive.Placement.NamespaceName
            let! cluster = source client archive ct
            let scheduleName = PostgresBackupNames.scheduledBackup archive.Location.ArchiveId
            let! schedule = get client PostgresBackupNames.Group ns "scheduledbackups" scheduleName ct
            let! store = get client PostgresBackupNames.StorageGroup ns "objectstores" (PostgresBackupNames.objectStore archive.Location.ArchiveId) ct
            if not (isNull store) then ensureArchive store archive
            let runs = ResizeArray<BackupObservedRun>()
            if not (isNull schedule) then
                ensureArchive schedule archive
                let mutable more = true
                let mutable token = null
                while more do
                    let! result = client.CustomObjects.ListNamespacedCustomObjectAsync(PostgresBackupNames.Group,"v1",ns,"backups",labelSelector=("cnpg.io/cluster=" + M.clusterName archive),limit=Nullable(200),continueParameter=token,cancellationToken=ct)
                    let doc = JsonSerializer.SerializeToNode(result)
                    for item in array doc ["items"] do
                        let owned = array item ["metadata";"ownerReferences"] |> Seq.exists (fun owner -> value owner ["uid"] = uid schedule && value owner ["kind"] = "ScheduledBackup")
                        if owned && value item ["spec";"cluster";"name"] = M.clusterName archive then
                            let observed = state item
                            let started = timestamp item ["status";"startedAt"]
                            let created = timestamp item ["metadata";"creationTimestamp"]
                            if not started.HasValue && not created.HasValue then fail "backup-status-unreadable" true
                            let failure = if observed = BackupObservedRunState.Failed then failureCode item else null
                            runs.Add(BackupObservedRun(uid item,value item ["status";"backupId"],observed,
                                                      started.GetValueOrDefault(created.GetValueOrDefault()),timestamp item ["status";"stoppedAt"],failure))
                    token <- value doc ["metadata";"continue"]
                    more <- not (String.IsNullOrEmpty token)
            let wal = if active cluster archive then condition cluster "ContinuousArchiving" else null
            let health =
                match value wal ["status"] with
                | "True" -> "Healthy"
                | "False" -> "Failing"
                | _ -> "Unknown"
            let next =
                if String.Equals(value schedule ["spec";"suspend"],"true",StringComparison.OrdinalIgnoreCase) then Nullable()
                else timestamp schedule ["status";"nextScheduleTime"]
            return BackupRuntimeObservation(health,timestamp wal ["lastTransitionTime"],
                        timestamp store ["status";"serverRecoveryWindow";archive.Location.ServerName;"lastSuccessfulBackupTime"],next,runs)
        }
