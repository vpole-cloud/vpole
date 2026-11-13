namespace Pole.Features.Postgres.Application.Backups

open System
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

module internal PostgresBackupRuntime =
    open PostgresBackupObjects
    open PostgresBackupArchive

    let observe client (archive: BackupArchiveContext) ct =
        task {
            let! loaded = source client archive ct
            match loaded with
            | Error SourceUnavailable -> return ObserveBackupRuntimeOutcome(BackupSourceUnavailable())
            | Error SourceOwnershipConflict -> return ObserveBackupRuntimeOutcome(BackupNativeOwnershipConflict())
            | Error SourceIdentityUnavailable -> return ObserveBackupRuntimeOutcome(BackupNativeIdentityUnavailable())
            | Ok(cluster, _) ->
                let ns = archive.Placement.NamespaceName
                let! schedule = get client PostgresBackupNames.Group ns "scheduledbackups"
                                    (PostgresBackupNames.scheduledBackup archive.Location.ArchiveId) ct
                let! store = get client PostgresBackupNames.StorageGroup ns "objectstores"
                                 (PostgresBackupNames.objectStore archive.Location.ArchiveId) ct
                if (not (isNull store) && not (ownsArchive store archive))
                   || (not (isNull schedule) && not (ownsArchive schedule archive)) then
                    return ObserveBackupRuntimeOutcome(BackupNativeOwnershipConflict())
                else
                    let wal = if active cluster archive then condition cluster "ContinuousArchiving" else null
                    let health =
                        match value wal ["status"] with
                        | "True" -> "Healthy"
                        | "False" -> "Failing"
                        | _ -> "Unknown"
                    let suspended =
                        String.Equals(value schedule ["spec"; "suspend"], "true", StringComparison.OrdinalIgnoreCase)
                    let enabled = not (isNull schedule) && not suspended
                    let next = if enabled then timestamp schedule ["status"; "nextScheduleTime"] else Nullable()
                    return ObserveBackupRuntimeOutcome(BackupRuntimeObservation(
                        health, timestamp wal ["lastTransitionTime"],
                        timestamp store ["status"; "serverRecoveryWindow"; archive.Location.ServerName;
                                         "lastSuccessfulBackupTime"], next, enabled))
        }
