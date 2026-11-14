namespace Pole.Features.Postgres.Application.Backups

open System
open System.Text.Json.Nodes
open k8s
open k8s.Models
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes

module internal PostgresBackupArchive =
    open PostgresBackupObjects
    module M = PostgresBackupManifests

    type ObjectFailure = OwnershipConflict | IdentityUnavailable
    type OwnedObject = Missing | Existing of JsonNode * Identity
    type SourceFailure = SourceUnavailable | SourceOwnershipConflict | SourceIdentityUnavailable

    let ownsSource current archive = labelsMatch current (M.sourceLabels archive)

    let ownsArchive current archive =
        ownsSource current archive
        && value current ["metadata"; "labels"; PostgresBackupNames.ArchiveLabel] = archive.Location.ArchiveId

    let source client (archive: BackupArchiveContext) ct =
        task {
            let! current =
                get client PostgresBackupNames.Group archive.Placement.NamespaceName
                    "clusters" (M.clusterName archive) ct
            if isNull current then return Error SourceUnavailable
            elif not (ownsSource current archive) then return Error SourceOwnershipConflict
            else
                return
                    match identity current with
                    | Some identity -> Ok(current, identity)
                    | None -> Error SourceIdentityUnavailable
        }

    let loadOwned client group (archive: BackupArchiveContext) plural (manifest: JsonNode) ct =
        task {
            let name = value manifest ["metadata"; "name"]
            let! current = get client group archive.Placement.NamespaceName plural name ct
            if isNull current then return Ok Missing
            else
                let desiredOwner = field manifest ["metadata"; "ownerReferences"]
                let sameOwner =
                    isNull desiredOwner
                    || JsonNode.DeepEquals(desiredOwner, field current ["metadata"; "ownerReferences"])
                if not (ownsArchive current archive) || not sameOwner then return Error OwnershipConflict
                else
                    return
                        match identity current with
                        | Some identity -> Ok(Existing(current, identity))
                        | None -> Error IdentityUnavailable
        }

    let saveOwned client group (archive: BackupArchiveContext) plural (manifest: JsonNode) observed ct =
        task {
            let ns = archive.Placement.NamespaceName
            match observed with
            | Missing -> return! create client group ns plural manifest ct
            | Existing(current, identity) ->
                let stamp = field manifest ["metadata"; "annotations"]
                let sameSpec = matches manifest["spec"] current["spec"]
                let sameStamp = isNull stamp || matches stamp (field current ["metadata"; "annotations"])
                if sameSpec && sameStamp then return current
                else
                    let body = M.node {| spec = manifest["spec"] |}
                    if not (isNull stamp) then body["metadata"] <- M.node {| annotations = stamp |}
                    let name = value manifest ["metadata"; "name"]
                    return! patch client group ns plural name identity body ct
        }

    let ensureStore client archive projection ct =
        task {
            let manifest = M.store archive projection None
            let! observed = loadOwned client PostgresBackupNames.StorageGroup archive "objectstores" manifest ct
            match observed with
            | Error error -> return Error error
            | Ok owned ->
                let! result = saveOwned client PostgresBackupNames.StorageGroup archive "objectstores" manifest owned ct
                return Ok result
        }

    let plugin current =
        array current ["spec"; "plugins"]
        |> Seq.tryFind (fun plugin -> value plugin ["name"] = PostgresBackupNames.PluginName)

    let active current (archive: BackupArchiveContext) =
        plugin current |> Option.exists (fun plugin ->
            value plugin ["parameters"; "barmanObjectName"] = PostgresBackupNames.objectStore archive.Location.ArchiveId
            && value plugin ["parameters"; "serverName"] = archive.Location.ServerName)

    // CNPG advertises pluginStatus before every ready instance has its Barman sidecar.
    // CNPG may choose a ready standby or fall back to the primary for a base backup.
    let podsReady (client: IKubernetes) (archive: BackupArchiveContext) cluster identity ct =
        task {
            let primary = value cluster ["status"; "currentPrimary"]
            let pluginLoaded =
                array cluster ["status"; "pluginStatus"]
                |> Seq.exists (fun plugin -> value plugin ["name"] = PostgresBackupNames.PluginName)
            if not pluginLoaded || value (condition cluster "Ready") ["status"] <> "True"
               || String.IsNullOrEmpty primary then return false
            else
                let selector = "cnpg.io/cluster=" + M.clusterName archive + ",cnpg.io/podRole=instance"
                let! pods = client.CoreV1.ListNamespacedPodAsync(
                    archive.Placement.NamespaceName, labelSelector = selector, cancellationToken = ct)
                let eligible =
                    pods.Items |> Seq.filter (fun pod ->
                        not pod.Metadata.DeletionTimestamp.HasValue
                        && not (isNull pod.Metadata.OwnerReferences)
                        && (pod.Metadata.OwnerReferences |> Seq.exists (fun owner ->
                            owner.Kind = "Cluster" && owner.Uid = identity.Uid))
                        && not (isNull pod.Status) && not (isNull pod.Status.Conditions)
                        && (pod.Status.Conditions |> Seq.exists (fun condition ->
                            condition.Type = "Ready" && condition.Status = "True"))) |> Seq.toArray
                let hasSidecar (pod: V1Pod) =
                    [pod.Status.InitContainerStatuses; pod.Status.ContainerStatuses]
                    |> Seq.filter (isNull >> not) |> Seq.concat
                    |> Seq.exists (fun status ->
                        status.Name = "plugin-barman-cloud" && status.Ready
                        && not (isNull status.State) && not (isNull status.State.Running))
                return (eligible |> Array.exists (fun pod -> pod.Metadata.Name = primary))
                       && Array.forall hasSidecar eligible
        }
