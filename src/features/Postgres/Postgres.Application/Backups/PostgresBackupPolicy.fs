// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Backups

open System
open System.Text.Json.Nodes
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

module internal PostgresBackupPolicy =
    open PostgresBackupObjects
    open PostgresBackupArchive
    module M = PostgresBackupManifests

    type Failure =
        | SourceMissing
        | NativeOwnershipConflict
        | NativeIdentityUnavailable
        | PolicyAlreadyActive
        | PolicyRevisionUnreadable
        | PolicySuperseded
        | PluginConflict

    type Progress = Applied | Pending | DrainRequired of string * NativeBackupScheduleSuspension
    type PolicyDisposition = Active | Retired

    // This annotation group belongs to this provider. Ordinary Cluster updates preserve it.
    let private policyKey = "backups.pole.io/policy-id"
    let private revisionKey = "backups.pole.io/policy-revision"
    let private retiredKey = "backups.pole.io/retired"

    let private annotation current key = value current ["metadata"; "annotations"; key]

    let private annotations (request: ApplyBackupScheduleRequest) disposition =
        dict [ policyKey, request.PolicyId
               revisionKey, string request.Revision
               retiredKey, (match disposition with Active -> "false" | Retired -> "true") ]

    let private guardRevision (request: ApplyBackupScheduleRequest) current disposition =
        let previous = annotation current policyKey
        let inactive = annotation current retiredKey = "true"
        if isNull previous then Ok()
        elif previous <> request.PolicyId then
            if inactive then Ok() else Error PolicyAlreadyActive
        else
            match Int64.TryParse(annotation current revisionKey) with
            | false, _ -> Error PolicyRevisionUnreadable
            | true, revision when revision > request.Revision || (inactive && disposition = Active) ->
                Error PolicySuperseded
            | true, _ -> Ok()

    let private objectFailure = function
        | OwnershipConflict -> NativeOwnershipConflict
        | IdentityUnavailable -> NativeIdentityUnavailable

    let private loadFenced client group archive plural (manifest: JsonNode) request disposition ct =
        task {
            let! loaded = loadOwned client group archive plural manifest ct
            return
                match loaded with
                | Error failure -> Error(objectFailure failure)
                | Ok Missing -> Ok Missing
                | Ok(Existing(current, _) as owned) ->
                    guardRevision request current disposition |> Result.map (fun () -> owned)
        }

    let private applyOwned client group archive plural (manifest: JsonNode) request disposition ct =
        task {
            manifest["metadata"]["annotations"] <- M.node (annotations request disposition)
            let! loaded = loadFenced client group archive plural manifest request disposition ct
            match loaded with
            | Error failure -> return Error failure
            | Ok owned ->
                let! result = saveOwned client group archive plural manifest owned ct
                return Ok result
        }

    let private parkSchedule client (request: ApplyBackupScheduleRequest) archiveId clusterUid ct =
        task {
            let archive = request.Archive
            let location = BackupArchiveLocation(archiveId, archive.Location.ServerName, archive.Location.Storage)
            let retired = BackupArchiveContext(location, archive.Source, archive.Service, archive.Placement)
            // This versioned tombstone also fences delayed creates. CNPG deletes it with the source.
            let tombstone = M.schedule retired (BackupSchedule("0 0 1 1 *", true, 1)) clusterUid
            let! result =
                applyOwned client PostgresBackupNames.Group retired "scheduledbackups" tombstone request Retired ct
            return result
        }

    let observedRevision current =
        match Int64.TryParse(annotation current revisionKey) with
        | true, revision when revision > 0L -> Some revision
        | _ -> None

    let suspension policyId current =
        match identity current, observedRevision current with
        | Some identity, Some revision
            when annotation current policyKey = policyId && annotation current retiredKey = "true"
                 && String.Equals(value current ["spec"; "suspend"], "true", StringComparison.OrdinalIgnoreCase) ->
            Some(NativeBackupScheduleSuspension(policyId, revision, identity.Uid))
        | _ -> None

    let private awaitDrain client (request: ApplyBackupScheduleRequest) archiveId clusterUid ct =
        task {
            let! parked = parkSchedule client request archiveId clusterUid ct
            match parked with
            | Error failure -> return Error failure
            | Ok schedule ->
                match suspension request.PolicyId schedule with
                | None -> return Error NativeIdentityUnavailable
                | Some fence ->
                    match request.Drain with
                    | :? ConfirmedBackupArchiveDrain as confirmed
                        when confirmed.ArchiveId = archiveId && confirmed.Suspension = fence -> return Ok None
                    | :? ConfirmedBackupArchiveDrain
                    | :? UnconfirmedBackupArchiveDrain -> return Ok(Some(DrainRequired(archiveId, fence)))
                    | _ -> return invalidOp "Unsupported archive drain authorization."
        }

    let private writePlugins client (request: ApplyBackupScheduleRequest) current identity disposition ct =
        task {
            let plugins = JsonArray()
            for plugin in array current ["spec"; "plugins"] do
                if value plugin ["name"] <> PostgresBackupNames.PluginName then plugins.Add(plugin.DeepClone())
            match disposition with
            | Active -> plugins.Add(M.walPlugin request.Archive)
            | Retired -> ()
            let stamp = annotations request disposition
            let previous = array current ["spec"; "plugins"] |> Seq.toArray
            let samePlugins = previous.Length = plugins.Count && Seq.forall2 matches plugins previous
            let sameStamp = stamp |> Seq.forall (fun pair -> annotation current pair.Key = pair.Value)
            if samePlugins && sameStamp then return current
            else
                let body = M.node {| spec = {| plugins = plugins |}; metadata = {| annotations = stamp |} |}
                return! patch client PostgresBackupNames.Group request.Archive.Placement.NamespaceName
                            "clusters" (M.clusterName request.Archive) identity body ct
        }

    let private previousArchive (request: ApplyBackupScheduleRequest) cluster =
        match plugin cluster with
        | None -> Ok None
        | Some plugin ->
            let store = value plugin ["parameters"; "barmanObjectName"]
            if isNull store || not (store.StartsWith(PostgresBackupNames.ArchivePrefix, StringComparison.Ordinal)) then
                Error PluginConflict
            elif store = PostgresBackupNames.objectStore request.Archive.Location.ArchiveId then Ok None
            else Ok(Some(store.Substring(PostgresBackupNames.ArchivePrefix.Length)))

    let private drainPrevious client request cluster identity ct =
        task {
            match previousArchive request cluster with
            | Error failure -> return Error failure
            | Ok None -> return Ok None
            | Ok(Some archiveId) -> return! awaitDrain client request archiveId identity.Uid ct
        }

    let private stop client (request: ApplyBackupScheduleRequest) cluster identity ct =
        task {
            let attached = annotation cluster policyKey
            let actual =
                match previousArchive request cluster with
                | Ok(Some previous) when attached = request.PolicyId -> Ok previous
                | Ok _ -> Ok request.Archive.Location.ArchiveId
                | Error failure -> Error failure
            match actual with
            | Error failure -> return Error failure
            | Ok archiveId ->
                let! drained = awaitDrain client request archiveId identity.Uid ct
                match drained with
                | Error failure -> return Error failure
                | Ok(Some progress) -> return Ok progress
                | Ok None ->
                    if attached = request.PolicyId || (isNull attached && (plugin cluster).IsNone) then
                        let! _ = writePlugins client request cluster identity Retired ct
                        ()
                    return Ok Applied
        }

    let private configureSchedule
        client (request: ApplyBackupScheduleRequest) (configuration: ConfigureBackupPolicy) cluster identity ct =
        task {
            let! updated = writePlugins client request cluster identity Active ct
            let! ready = podsReady client request.Archive updated identity ct
            let archiving = value (condition updated "ContinuousArchiving") ["status"] = "True"
            let desired = configuration.Schedule
            let schedule =
                BackupSchedule(desired.CronUtc, desired.Suspended || not (ready && archiving), desired.RetentionDays)
            let manifest = M.schedule request.Archive schedule identity.Uid
            let! scheduled =
                applyOwned client PostgresBackupNames.Group request.Archive
                    "scheduledbackups" manifest request Active ct
            return
                scheduled |> Result.map (fun _ ->
                    if ready && archiving then Applied else Pending)
        }

    let private configureStore
        client (request: ApplyBackupScheduleRequest) (configuration: ConfigureBackupPolicy) cluster identity ct =
        task {
            let manifest = M.store request.Archive configuration.Projection (Some configuration.Schedule.RetentionDays)
            let! stored =
                applyOwned client PostgresBackupNames.StorageGroup request.Archive
                    "objectstores" manifest request Active ct
            match stored with
            | Error failure -> return Error failure
            | Ok _ ->
                if (plugin cluster).IsSome && annotation cluster policyKey <> request.PolicyId then
                    return Error PluginConflict
                else
                    let! parked = drainPrevious client request cluster identity ct
                    match parked with
                    | Error failure -> return Error failure
                    | Ok(Some progress) -> return Ok progress
                    | Ok None -> return! configureSchedule client request configuration cluster identity ct
        }

    let private configure
        client (request: ApplyBackupScheduleRequest) (configuration: ConfigureBackupPolicy) cluster identity ct =
        task {
            // Read the tombstone after observing Cluster.resourceVersion, before any write.
            let manifest = M.schedule request.Archive configuration.Schedule identity.Uid
            let! previous =
                loadFenced client PostgresBackupNames.Group request.Archive
                    "scheduledbackups" manifest request Active ct
            match previous with
            | Error failure -> return Error failure
            | Ok _ -> return! configureStore client request configuration cluster identity ct
        }

    let apply client (request: ApplyBackupScheduleRequest) ct =
        task {
            let! loaded = source client request.Archive ct
            match loaded with
            | Error SourceUnavailable -> return Error SourceMissing
            | Error SourceOwnershipConflict -> return Error NativeOwnershipConflict
            | Error SourceIdentityUnavailable -> return Error NativeIdentityUnavailable
            | Ok(cluster, identity) ->
                let disposition =
                    match request.Configuration with
                    | :? StopBackupPolicy -> Retired
                    | :? ConfigureBackupPolicy -> Active
                    | _ -> invalidOp "Unsupported backup policy configuration."
                match guardRevision request cluster disposition with
                | Error failure -> return Error failure
                | Ok() ->
                    match request.Configuration with
                    | :? StopBackupPolicy -> return! stop client request cluster identity ct
                    | :? ConfigureBackupPolicy as configuration ->
                        return! configure client request configuration cluster identity ct
                    | _ -> return invalidOp "Unsupported backup policy configuration."
        }

    let outcome = function
        | Ok Applied -> ApplyBackupScheduleOutcome(BackupPolicyApplied())
        | Ok Pending -> ApplyBackupScheduleOutcome(BackupPolicyPending())
        | Ok(DrainRequired(archiveId, fence)) -> ApplyBackupScheduleOutcome(BackupPolicyDrainRequired(archiveId, fence))
        | Error SourceMissing -> ApplyBackupScheduleOutcome(BackupSourceUnavailable())
        | Error NativeOwnershipConflict -> ApplyBackupScheduleOutcome(BackupNativeOwnershipConflict())
        | Error NativeIdentityUnavailable -> ApplyBackupScheduleOutcome(BackupNativeIdentityUnavailable())
        | Error PolicyAlreadyActive -> ApplyBackupScheduleOutcome(BackupPolicyAlreadyActive())
        | Error PolicyRevisionUnreadable -> ApplyBackupScheduleOutcome(BackupPolicyRevisionUnreadable())
        | Error PolicySuperseded -> ApplyBackupScheduleOutcome(BackupPolicySuperseded())
        | Error PluginConflict -> ApplyBackupScheduleOutcome(BackupPluginConflict())

    let suspend client (request: SuspendBackupArchiveRequest) ct =
        task {
            let! loaded = source client request.Archive ct
            match loaded with
            | Error SourceUnavailable -> return SuspendBackupArchiveOutcome(BackupSourceUnavailable())
            | Error SourceOwnershipConflict -> return SuspendBackupArchiveOutcome(BackupNativeOwnershipConflict())
            | Error SourceIdentityUnavailable -> return SuspendBackupArchiveOutcome(BackupNativeIdentityUnavailable())
            | Ok(_, identity) ->
                let command = ApplyBackupScheduleRequest(
                    request.PolicyId, request.Archive, StopBackupPolicy(), request.PolicyRevision,
                    UnconfirmedBackupArchiveDrain())
                let manifest = M.schedule request.Archive (BackupSchedule("0 0 1 1 *", true, 1)) identity.Uid
                let! existing = loadOwned client PostgresBackupNames.Group request.Archive "scheduledbackups" manifest ct
                let! parked =
                    match existing with
                    | Error failure -> System.Threading.Tasks.Task.FromResult(Error(objectFailure failure))
                    | Ok(Existing(current, _)) ->
                        match suspension request.PolicyId current with
                        | Some fence when fence.PolicyRevision >= request.PolicyRevision ->
                            System.Threading.Tasks.Task.FromResult(Ok current)
                        | _ -> parkSchedule client command request.Archive.Location.ArchiveId identity.Uid ct
                    | Ok Missing -> parkSchedule client command request.Archive.Location.ArchiveId identity.Uid ct
                return
                    match parked with
                    | Ok schedule ->
                        match suspension request.PolicyId schedule with
                        | Some fence -> SuspendBackupArchiveOutcome(BackupArchiveSuspended(fence))
                        | None -> SuspendBackupArchiveOutcome(BackupNativeIdentityUnavailable())
                    | Error NativeOwnershipConflict -> SuspendBackupArchiveOutcome(BackupNativeOwnershipConflict())
                    | Error NativeIdentityUnavailable -> SuspendBackupArchiveOutcome(BackupNativeIdentityUnavailable())
                    | Error PolicyRevisionUnreadable -> SuspendBackupArchiveOutcome(BackupPolicyRevisionUnreadable())
                    | Error PolicySuperseded -> SuspendBackupArchiveOutcome(BackupPolicySuperseded())
                    | Error PolicyAlreadyActive -> SuspendBackupArchiveOutcome(BackupNativeOwnershipConflict())
                    | Error SourceMissing | Error PluginConflict ->
                        invalidOp "Suspending a schedule must not mutate a Cluster plugin."
        }
