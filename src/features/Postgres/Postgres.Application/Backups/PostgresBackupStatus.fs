namespace Pole.Features.Postgres.Application.Backups

open System
open Pole.Features.Backups.Contracts

module internal PostgresBackupStatus =
    open PostgresBackupObjects

    type Failure =
        | PluginUnavailable
        | StorageAccessDenied
        | StorageUnreachable
        | DefinitionInvalid
        | ExecutionFailed

    type State = Running | Completed of string | Failed of Failure

    let code = function
        | PluginUnavailable -> "backup-plugin-unavailable"
        | StorageAccessDenied -> "backup-storage-access-denied"
        | StorageUnreachable -> "backup-storage-unreachable"
        | DefinitionInvalid -> "backup-definition-invalid"
        | ExecutionFailed -> "backup-execution-failed"

    let private failure native =
        // CNPG exposes plugin errors as unstructured text. Classify known causes here;
        // never return the native message, which may contain storage credentials.
        let message = value native ["status"; "error"] |> Option.ofObj |> Option.defaultValue ""
        let contains (tokens: string list) =
            tokens |> List.exists (fun token -> message.Contains(token, StringComparison.OrdinalIgnoreCase))
        if contains ["requested plugin is not available"; "unknown plugin"; "error loading plugin"] then PluginUnavailable
        elif contains ["AccessDenied"; "InvalidAccessKeyId"; "SignatureDoesNotMatch"] then StorageAccessDenied
        elif contains ["connection refused"; "no such host"; "could not connect to the endpoint"] then StorageUnreachable
        elif value native ["status"; "phase"] = "invalid backup definition" then DefinitionInvalid
        else ExecutionFailed

    let read native =
        match value native ["status"; "phase"] with
        | "completed" ->
            let id = value native ["status"; "backupId"]
            if String.IsNullOrWhiteSpace id then Error(BackupStatusUnreadable())
            else Ok(Completed id)
        | "failed" | "invalid backup definition" -> Ok(Failed(failure native))
        | _ -> Ok Running
