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

    type Progress = Applied | Pending
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
            return result |> Result.map ignore
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

    let private parkPrevious client request cluster identity ct =
        task {
            match previousArchive request cluster with
            | Error failure -> return Error failure
            | Ok None -> return Ok()
            | Ok(Some archiveId) -> return! parkSchedule client request archiveId identity.Uid ct
        }

    let private stop client (request: ApplyBackupScheduleRequest) cluster identity ct =
        task {
            let! parked = parkSchedule client request request.Archive.Location.ArchiveId identity.Uid ct
            match parked with
            | Error failure -> return Error failure
            | Ok() ->
                let attached = annotation cluster policyKey
                let! previous =
                    if attached = request.PolicyId then parkPrevious client request cluster identity ct
                    else System.Threading.Tasks.Task.FromResult(Ok())
                match previous with
                | Error failure -> return Error failure
                | Ok() ->
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
                    let! parked = parkPrevious client request cluster identity ct
                    match parked with
                    | Error failure -> return Error failure
                    | Ok() -> return! configureSchedule client request configuration cluster identity ct
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
        | Error SourceMissing -> ApplyBackupScheduleOutcome(BackupSourceUnavailable())
        | Error NativeOwnershipConflict -> ApplyBackupScheduleOutcome(BackupNativeOwnershipConflict())
        | Error NativeIdentityUnavailable -> ApplyBackupScheduleOutcome(BackupNativeIdentityUnavailable())
        | Error PolicyAlreadyActive -> ApplyBackupScheduleOutcome(BackupPolicyAlreadyActive())
        | Error PolicyRevisionUnreadable -> ApplyBackupScheduleOutcome(BackupPolicyRevisionUnreadable())
        | Error PolicySuperseded -> ApplyBackupScheduleOutcome(BackupPolicySuperseded())
        | Error PluginConflict -> ApplyBackupScheduleOutcome(BackupPluginConflict())
