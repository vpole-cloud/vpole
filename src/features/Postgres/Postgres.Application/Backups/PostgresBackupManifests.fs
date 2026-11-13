namespace Pole.Features.Postgres.Application.Backups

open Pole.Scheduling

open System.Text.Json
open System.Text.Json.Nodes
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.StorageProfiles.Contracts
open Pole.Workloads

[<RequireQualifiedAccess>]
module PostgresBackupManifests =
    let pluginName = "barman-cloud.cloudnative-pg.io"
    let group = "postgresql.cnpg.io"
    let storageGroup = "barmancloud.cnpg.io"
    let storeName id = "archive-" + id
    let scheduleName id = "schedule-" + id
    let clusterName (archive: BackupArchiveContext) = PostgresClusterKubernetesBuilder.buildResourceName archive.Source.ResourceId
    let archiveLabel = "pole.io/backup-archive-id"

    let sourceLabels (archive: BackupArchiveContext) =
        [ PoleStorageLabels.TenantId, archive.Source.TenantId
          PoleStorageLabels.ResourceId, archive.Source.ResourceId
          PoleStorageLabels.ResourceGroupId, archive.Placement.ResourceGroupId
          "pole.io/resource-deployment-id", archive.Placement.DeploymentId
          "pole.io/deployment-target-id", archive.Placement.DeploymentTargetId
          "pole.io/runtime-cell-id", archive.Placement.RuntimeCellId
          "pole.io/isolation-scope-id", archive.Placement.IsolationScopeId ]

    let node value = JsonSerializer.SerializeToNode(value)
    let private resource api kind name (archive: BackupArchiveContext) spec =
        node {| apiVersion = api + "/v1"
                kind = kind
                metadata = {| name = name
                              ``namespace`` = archive.Placement.NamespaceName
                              labels = dict ((archiveLabel, archive.ArchiveId) :: sourceLabels archive) |}
                spec = spec |}

    let store (archive: BackupArchiveContext) (retention: int option) =
        let key k = {| name = archive.NativeCredentialName; key = k |}
        let path = ("s3://" + archive.Bucket + "/" + archive.Prefix.Trim('/')).TrimEnd('/')
        let config =
            {| destinationPath = path
               endpointURL = archive.Endpoint
               s3Credentials = {| accessKeyId = key archive.AccessKeyIdKey; secretAccessKey = key archive.SecretAccessKeyKey |} |}
        let spec = node {| configuration = config |}
        retention |> Option.iter (fun days -> spec["retentionPolicy"] <- node $"{days}d")
        resource storageGroup "ObjectStore" (storeName archive.ArchiveId) archive spec

    let walPlugin (archive: BackupArchiveContext) =
        node {| name = pluginName
                isWALArchiver = true
                parameters = {| barmanObjectName = storeName archive.ArchiveId; serverName = archive.ServerName |} |}

    let private backupSpec archive =
        node {| cluster = {| name = clusterName archive |}
                method = "plugin"
                pluginConfiguration = {| name = pluginName |} |}

    let private withOwner (manifest: JsonNode) archive uid =
        manifest["metadata"]["ownerReferences"] <-
            node [| {| apiVersion = group + "/v1"; kind = "Cluster"; name = clusterName archive; uid = uid |} |]
        manifest

    let schedule (archive: BackupArchiveContext) (policy: BackupSchedule) (clusterUid: string) =
        let spec = backupSpec archive
        spec["schedule"] <- node ("0 " + UtcCronSchedule.Parse(policy.CronUtc).ToCanonicalExpression())
        spec["suspend"] <- node policy.Suspended
        spec["backupOwnerReference"] <- node "self"
        withOwner (resource group "ScheduledBackup" (scheduleName archive.ArchiveId) archive spec) archive clusterUid

    let backup (request: BackupCopyRequest) (clusterUid: string) =
        withOwner
            (resource group "Backup" ("backup-" + request.OperationId) request.Archive (backupSpec request.Archive))
            request.Archive clusterUid
