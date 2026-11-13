namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open k8s.Models
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

exception internal PostgresBackupFailure of string * bool

module internal BackupNative =
    module M = PostgresBackupManifests

    let fail code retryable = raise (PostgresBackupFailure(code, retryable))
    let field (doc: JsonNode) (keys: string list) =
        keys |> List.fold (fun (parent: JsonNode) key -> if isNull parent then null else parent[key]) doc
    let value doc keys =
        let n = field doc keys
        if isNull n then null else n.ToString()
    let array doc keys =
        match field doc keys with
        | :? JsonArray as items -> items |> Seq.cast<JsonNode>
        | _ -> Seq.empty
    let uid doc =
        let identity = value doc ["metadata"; "uid"]
        if String.IsNullOrWhiteSpace identity then fail "backup-native-identity-missing" true
        identity
    let resourceVersion doc =
        let version = value doc ["metadata"; "resourceVersion"]
        if String.IsNullOrWhiteSpace version then fail "backup-native-identity-missing" true
        version

    let get (client: IKubernetes) group ns plural name ct =
        task {
            try
                let! obj = client.CustomObjects.GetNamespacedCustomObjectAsync(group,"v1",ns,plural,name,cancellationToken=ct)
                return JsonSerializer.SerializeToNode(obj)
            with :? HttpOperationException as e when e.Response.StatusCode = HttpStatusCode.NotFound -> return null
        }

    let labelsMatch doc labels =
        labels |> List.forall (fun (key,expected) -> value doc ["metadata";"labels";key] = expected)
    let ensureSource doc archive =
        if not (labelsMatch doc (M.sourceLabels archive)) then fail "backup-resource-ownership-conflict" false
    let ensureArchive doc archive =
        ensureSource doc archive
        if value doc ["metadata";"labels";PostgresBackupNames.ArchiveLabel] <> archive.Location.ArchiveId then fail "backup-resource-ownership-conflict" false

    let source client (archive: BackupArchiveContext) ct =
        task {
            let! current = get client PostgresBackupNames.Group archive.Placement.NamespaceName "clusters" (M.clusterName archive) ct
            if isNull current then fail "backup-source-unavailable" true
            ensureSource current archive
            return current
        }

    let create (client: IKubernetes) group ns plural (manifest: JsonNode) ct =
        task {
            let! result = client.CustomObjects.CreateNamespacedCustomObjectAsync(manifest,group,"v1",ns,plural,cancellationToken=ct)
            return JsonSerializer.SerializeToNode(result)
        }
    let patch (client: IKubernetes) group ns plural name current (body: JsonNode) ct =
        task {
            if isNull body["metadata"] then body["metadata"] <- JsonObject()
            body["metadata"]["resourceVersion"] <- M.node (resourceVersion current)
            body["metadata"]["uid"] <- M.node (uid current)
            let patch = V1Patch(body.ToJsonString(), V1Patch.PatchType.MergePatch)
            let! result = client.CustomObjects.PatchNamespacedCustomObjectAsync(patch,group,"v1",ns,plural,name,cancellationToken=ct)
            return JsonSerializer.SerializeToNode(result)
        }

    // This annotation group is solely owned by the backup provider. Ordinary Cluster updates preserve it.
    let policyKey = "backups.pole.io/policy-id"
    let revisionKey = "backups.pole.io/policy-revision"
    let retiredKey = "backups.pole.io/retired"

    let annotation doc key = value doc ["metadata";"annotations";key]
    let annotations (request: ApplyBackupScheduleRequest) retired =
        dict [ policyKey, request.PolicyId; revisionKey, string request.Revision; retiredKey, (if retired then "true" else "false") ]

    let guardRevision (request: ApplyBackupScheduleRequest) current retired =
        let previous = annotation current policyKey
        if not (isNull previous) then
            let inactive = annotation current retiredKey = "true"
            if previous <> request.PolicyId && not inactive then fail "backup-policy-already-active" true
            if previous = request.PolicyId then
                let valid, revision = Int64.TryParse(annotation current revisionKey)
                if not valid then fail "backup-policy-revision-unreadable" false
                if revision > request.Revision || (inactive && not retired) then fail "backup-policy-superseded" false

    let rec matches (desired: JsonNode) (current: JsonNode) =
        match desired, current with
        | (:? JsonObject as fields), (:? JsonObject as actual) ->
            fields |> Seq.forall (fun kv -> matches kv.Value actual[kv.Key])
        | _ -> JsonNode.DeepEquals(desired,current)

    let applyOwned client group (archive: BackupArchiveContext) plural (manifest: JsonNode) fence ct =
        task {
            let ns = archive.Placement.NamespaceName
            let name = value manifest ["metadata";"name"]
            let! current = get client group ns plural name ct
            for request, retired in Option.toList fence do
                manifest["metadata"]["annotations"] <- M.node (annotations request retired)
                if not (isNull current) then guardRevision request current retired
            if isNull current then
                return! create client group ns plural manifest ct
            else
                ensureArchive current archive
                let owner = field manifest ["metadata";"ownerReferences"]
                if not (isNull owner) && not (JsonNode.DeepEquals(owner,current["metadata"]["ownerReferences"])) then
                    fail "backup-resource-ownership-conflict" false
                let stamp = field manifest ["metadata";"annotations"]
                let changed = not (matches manifest["spec"] current["spec"])
                if changed || (not (isNull stamp) && not (matches stamp (field current ["metadata";"annotations"]))) then
                    let body = M.node {|spec = manifest["spec"]|}
                    if not (isNull stamp) then body["metadata"] <- M.node {| annotations = stamp |}
                    return! patch client group ns plural name current body ct
                else return current
        }

    let ensureStore client archive projection ct =
        applyOwned client PostgresBackupNames.StorageGroup archive "objectstores" (M.store archive projection None) None ct

    let parkSchedule client (request: ApplyBackupScheduleRequest) archiveId clusterUid ct =
        task {
            let a = request.Archive
            let location = BackupArchiveLocation(archiveId, a.Location.ServerName, a.Location.Storage)
            let retired = BackupArchiveContext(location, a.Source, a.Service, a.Placement)
            // A suspended, versioned object fences delayed creates too. CNPG garbage-collects it with its source.
            let tombstone = M.schedule retired (BackupSchedule("0 0 1 1 *",true,1)) clusterUid
            let! _ = applyOwned client PostgresBackupNames.Group retired "scheduledbackups" tombstone (Some(request,true)) ct
            return ()
        }

    let plugin current =
        array current ["spec";"plugins"] |> Seq.tryFind (fun p -> value p ["name"] = PostgresBackupNames.PluginName)
    let active current (archive: BackupArchiveContext) =
        plugin current |> Option.exists (fun p ->
            value p ["parameters";"barmanObjectName"] = PostgresBackupNames.objectStore archive.Location.ArchiveId && value p ["parameters";"serverName"] = archive.Location.ServerName)

    let pluginLoaded cluster =
        array cluster ["status";"pluginStatus"] |> Seq.exists (fun p -> value p ["name"] = PostgresBackupNames.PluginName)

    let private writePlugins client (request: ApplyBackupScheduleRequest) current enable ct =
        task {
            let archive = request.Archive
            let next = JsonArray()
            for p in array current ["spec";"plugins"] do
                if value p ["name"] <> PostgresBackupNames.PluginName then next.Add(p.DeepClone())
            if enable then next.Add(M.walPlugin archive)
            let stamp = annotations request (not enable)
            let currentPlugins = array current ["spec";"plugins"] |> Seq.toArray
            let samePlugins = currentPlugins.Length = next.Count && Seq.forall2 matches next currentPlugins
            if samePlugins && (stamp |> Seq.forall (fun kv -> annotation current kv.Key = kv.Value)) then return current
            else
                let body = M.node {| spec = {| plugins = next |}; metadata = {| annotations = stamp |} |}
                return! patch client PostgresBackupNames.Group archive.Placement.NamespaceName "clusters" (M.clusterName archive) current body ct
        }

    let condition doc kind =
        array doc ["status";"conditions"] |> Seq.tryFind (fun c -> value c ["type"] = kind) |> Option.toObj

    // CNPG advertises pluginStatus before it has rolled existing instances to the injected Barman sidecar.
    // CNPG can choose a ready standby for a base backup, or fall back to the primary.
    let backupPodsReady (client: IKubernetes) (archive: BackupArchiveContext) cluster ct =
        task {
            let primary = value cluster ["status";"currentPrimary"]
            if not (pluginLoaded cluster) || value (condition cluster "Ready") ["status"] <> "True" || String.IsNullOrEmpty primary then
                return false
            else
                let clusterUid = uid cluster
                let selector = "cnpg.io/cluster=" + M.clusterName archive + ",cnpg.io/podRole=instance"
                let! pods = client.CoreV1.ListNamespacedPodAsync(archive.Placement.NamespaceName,labelSelector=selector,cancellationToken=ct)
                let eligible =
                    pods.Items |> Seq.filter (fun pod ->
                        not pod.Metadata.DeletionTimestamp.HasValue && not (isNull pod.Metadata.OwnerReferences) &&
                        (pod.Metadata.OwnerReferences |> Seq.exists (fun owner -> owner.Kind = "Cluster" && owner.Uid = clusterUid)) &&
                        not (isNull pod.Status) && not (isNull pod.Status.Conditions) &&
                        (pod.Status.Conditions |> Seq.exists (fun c -> c.Type = "Ready" && c.Status = "True"))) |> Seq.toArray
                let hasSidecar (pod: V1Pod) =
                    [pod.Status.InitContainerStatuses; pod.Status.ContainerStatuses]
                    |> Seq.filter (isNull >> not) |> Seq.concat
                    |> Seq.exists (fun status -> status.Name = "plugin-barman-cloud" && status.Ready &&
                                                 not (isNull status.State) && not (isNull status.State.Running))
                return (eligible |> Array.exists (fun p -> p.Metadata.Name = primary)) && Array.forall hasSidecar eligible
        }

    let apply client (request: ApplyBackupScheduleRequest) ct =
        task {
            let a = request.Archive
            let! cluster = source client a ct
            let retiring = request.Configuration :? StopBackupPolicy
            guardRevision request cluster retiring
            let identity = uid cluster
            match request.Configuration with
            | :? StopBackupPolicy ->
                do! parkSchedule client request a.Location.ArchiveId identity ct
                let attachedPolicy = annotation cluster policyKey
                if attachedPolicy = request.PolicyId then
                    // The latest archive may never have attached if deletion won a race with a destination change.
                    match plugin cluster with
                    | Some p ->
                        let store = value p ["parameters";"barmanObjectName"]
                        if isNull store || not (store.StartsWith(PostgresBackupNames.ArchivePrefix,StringComparison.Ordinal)) then
                            fail "backup-existing-plugin-conflict" false
                        if store <> PostgresBackupNames.objectStore a.Location.ArchiveId then
                            do! parkSchedule client request (store.Substring(PostgresBackupNames.ArchivePrefix.Length)) identity ct
                    | None -> ()
                if attachedPolicy = request.PolicyId || (isNull attachedPolicy && (plugin cluster).IsNone) then
                    let! _ = writePlugins client request cluster false ct
                    ()
                return BackupProviderProgress(BackupProviderPhase.Succeeded,null)
            | :? ConfigureBackupPolicy as configuration ->
                // Check the per-archive tombstone *after* reading the Cluster's resourceVersion.
                // An older request can neither reattach a retired archive nor re-enable its schedule.
                let! previousSchedule = get client PostgresBackupNames.Group a.Placement.NamespaceName "scheduledbackups" (PostgresBackupNames.scheduledBackup a.Location.ArchiveId) ct
                if not (isNull previousSchedule) then
                    ensureArchive previousSchedule a
                    guardRevision request previousSchedule false
                let store = M.store a configuration.Projection (Some configuration.Schedule.RetentionDays)
                let! _ = applyOwned client PostgresBackupNames.StorageGroup a "objectstores" store (Some(request,false)) ct
                match plugin cluster with
                | Some p ->
                    if annotation cluster policyKey <> request.PolicyId then fail "backup-existing-plugin-conflict" false
                    let old = value p ["parameters";"barmanObjectName"]
                    if isNull old || not (old.StartsWith(PostgresBackupNames.ArchivePrefix,StringComparison.Ordinal)) then
                        fail "backup-existing-plugin-conflict" false
                    if old <> PostgresBackupNames.objectStore a.Location.ArchiveId then
                        do! parkSchedule client request (old.Substring(PostgresBackupNames.ArchivePrefix.Length)) identity ct
                | None -> ()
                let! updated = writePlugins client request cluster true ct
                let! ready = backupPodsReady client a updated ct
                let desired = configuration.Schedule
                let scheduled = M.schedule a (BackupSchedule(desired.CronUtc,desired.Suspended || not ready,desired.RetentionDays)) identity
                let! _ = applyOwned client PostgresBackupNames.Group a "scheduledbackups" scheduled (Some(request,false)) ct
                let archiving = value (condition updated "ContinuousArchiving") ["status"]
                let phase = if ready && archiving = "True" then BackupProviderPhase.Succeeded else BackupProviderPhase.Running
                return BackupProviderProgress(phase,null)
            | _ -> return invalidOp "Unsupported backup policy configuration."
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
                        let! ready = backupPodsReady client a cluster ct
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

    let timestamp doc keys =
        match DateTimeOffset.TryParse(value doc keys,System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind) with
        | true, parsed -> Nullable(parsed.ToUniversalTime())
        | false, _ -> Nullable()

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
