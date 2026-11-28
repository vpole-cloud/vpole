// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Backups

open System.Net
open k8s.Autorest
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

module internal PostgresBackupCopy =
    open PostgresBackupObjects
    open PostgresBackupArchive
    module M = PostgresBackupManifests
    module Status = PostgresBackupStatus

    type private ObservationFailure =
        | OwnershipConflict
        | IdentityUnavailable
        | StatusUnreadable
        | ExecutionFailure of Status.Failure

    type private Progress = Running | Completed of string

    let private existing archive sourceIdentity native =
        let owned =
            array native ["metadata"; "ownerReferences"]
            |> Seq.exists (fun owner -> value owner ["kind"] = "Cluster" && value owner ["uid"] = sourceIdentity.Uid)
        if not (ownsArchive native archive) || not owned
           || value native ["spec"; "cluster"; "name"] <> M.clusterName archive then
            Error OwnershipConflict
        elif (identity native).IsNone then Error IdentityUnavailable
        else
            match Status.read native with
            | Ok Status.Running -> Ok Running
            | Ok(Status.Completed id) -> Ok(Completed id)
            | Ok(Status.Failed failure) -> Error(ExecutionFailure failure)
            | Error _ -> Error StatusUnreadable

    let private observation = function
        | Ok Running -> ObserveBackupCopyOutcome(BackupCopyInProgress())
        | Ok(Completed id) -> ObserveBackupCopyOutcome(BackupCopyCompleted(id))
        | Error OwnershipConflict -> ObserveBackupCopyOutcome(BackupNativeOwnershipConflict())
        | Error IdentityUnavailable -> ObserveBackupCopyOutcome(BackupNativeIdentityUnavailable())
        | Error StatusUnreadable -> ObserveBackupCopyOutcome(BackupStatusUnreadable())
        | Error(ExecutionFailure Status.PluginUnavailable) -> ObserveBackupCopyOutcome(BackupPluginUnavailable())
        | Error(ExecutionFailure Status.StorageAccessDenied) -> ObserveBackupCopyOutcome(BackupStorageAccessDenied())
        | Error(ExecutionFailure Status.StorageUnreachable) -> ObserveBackupCopyOutcome(BackupStorageUnreachable())
        | Error(ExecutionFailure Status.DefinitionInvalid) -> ObserveBackupCopyOutcome(BackupDefinitionInvalid())
        | Error(ExecutionFailure Status.ExecutionFailed) -> ObserveBackupCopyOutcome(BackupExecutionFailed())

    let private execution = function
        | Ok Running -> RunBackupCopyOutcome(BackupCopyInProgress())
        | Ok(Completed id) -> RunBackupCopyOutcome(BackupCopyCompleted(id))
        | Error OwnershipConflict -> RunBackupCopyOutcome(BackupNativeOwnershipConflict())
        | Error IdentityUnavailable -> RunBackupCopyOutcome(BackupNativeIdentityUnavailable())
        | Error StatusUnreadable -> RunBackupCopyOutcome(BackupStatusUnreadable())
        | Error(ExecutionFailure Status.PluginUnavailable) -> RunBackupCopyOutcome(BackupPluginUnavailable())
        | Error(ExecutionFailure Status.StorageAccessDenied) -> RunBackupCopyOutcome(BackupStorageAccessDenied())
        | Error(ExecutionFailure Status.StorageUnreachable) -> RunBackupCopyOutcome(BackupStorageUnreachable())
        | Error(ExecutionFailure Status.DefinitionInvalid) -> RunBackupCopyOutcome(BackupDefinitionInvalid())
        | Error(ExecutionFailure Status.ExecutionFailed) -> RunBackupCopyOutcome(BackupExecutionFailed())

    let observe client (request: ObserveBackupCopyRequest) ct =
        task {
            let archive = request.Archive
            let! loaded = source client archive ct
            match loaded with
            | Error SourceUnavailable -> return ObserveBackupCopyOutcome(BackupSourceUnavailable())
            | Error SourceOwnershipConflict -> return ObserveBackupCopyOutcome(BackupNativeOwnershipConflict())
            | Error SourceIdentityUnavailable -> return ObserveBackupCopyOutcome(BackupNativeIdentityUnavailable())
            | Ok(_, sourceIdentity) ->
                let! current =
                    get client PostgresBackupNames.Group archive.Placement.NamespaceName
                        "backups" (PostgresBackupNames.manualBackup request.OperationId) ct
                return
                    if isNull current then ObserveBackupCopyOutcome(BackupCopyMissing())
                    else existing archive sourceIdentity current |> observation
        }

    let private createCopy client (request: BackupCopyRequest) sourceIdentity ct =
        task {
            try
                let archive = request.Archive
                let! native =
                    create client PostgresBackupNames.Group archive.Placement.NamespaceName
                        "backups" (M.backup request sourceIdentity.Uid) ct
                return existing archive sourceIdentity native |> execution
            with
            | :? HttpOperationException as error when error.Response.StatusCode = HttpStatusCode.Conflict ->
                return RunBackupCopyOutcome(BackupNativeWriteConflict())
        }

    let run client (request: BackupCopyRequest) ct =
        task {
            let archive = request.Archive
            let! loaded = source client archive ct
            match loaded with
            | Error SourceUnavailable -> return RunBackupCopyOutcome(BackupSourceUnavailable())
            | Error SourceOwnershipConflict -> return RunBackupCopyOutcome(BackupNativeOwnershipConflict())
            | Error SourceIdentityUnavailable -> return RunBackupCopyOutcome(BackupNativeIdentityUnavailable())
            | Ok(cluster, sourceIdentity) ->
                let! current =
                    get client PostgresBackupNames.Group archive.Placement.NamespaceName
                        "backups" (PostgresBackupNames.manualBackup request.OperationId) ct
                if not (isNull current) then return existing archive sourceIdentity current |> execution
                elif not (active cluster archive) then return RunBackupCopyOutcome(BackupArchiveInactive())
                else
                    let! ready = podsReady client archive cluster sourceIdentity ct
                    if not ready then return RunBackupCopyOutcome(BackupPluginNotReady())
                    else return! createCopy client request sourceIdentity ct
        }
