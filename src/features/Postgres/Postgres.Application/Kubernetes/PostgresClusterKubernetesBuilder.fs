// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Secrets.Contracts
open Pole.Features.Postgres.Application.Connections
open Pole.Features.StorageProfiles.Contracts
open k8s.Models

type PostgresResourceDeploymentMetadata =
    { DeploymentId: string
      IsolationScopeId: string
      RuntimeCellId: string
      DeploymentTargetId: string
      Generation: int64 }

[<RequireQualifiedAccess>]
module PostgresClusterKubernetesBuilder =
    let buildResourceName (clusterId: string) = $"pg-{clusterId}"

    let buildReadWriteServiceName (clusterId: string) =
        PostgresConnectionEndpoints.buildReadWriteServiceName clusterId

    let buildLabels (cluster: PostgresCluster) (metadata: PostgresResourceDeploymentMetadata) =
        dict
            [ "app", cluster.Id
              "component", "postgres"
              PoleStorageLabels.TenantId, cluster.TenantId
              PoleStorageLabels.ResourceGroupId, cluster.GroupId
              PoleStorageLabels.ResourceType, "Postgres"
              PoleStorageLabels.ResourceId, cluster.Id
              PoleStorageLabels.ResourceName, cluster.Name
              "pole.io/resource-deployment-id", metadata.DeploymentId
              "pole.io/isolation-scope-id", metadata.IsolationScopeId
              "pole.io/runtime-cell-id", metadata.RuntimeCellId
              "pole.io/deployment-target-id", metadata.DeploymentTargetId
              "pole.io/deployment-generation", string metadata.Generation ]

    let buildAnnotations () =
        dict [ PoleStorageLabels.ResourceType, PostgresResourceDeployment.ResourceType.Value ]

    let private toResourceDictionary (resources: IDictionary<string, ResourceQuantity>) =
        let values = Dictionary<string, string>()

        for KeyValue(key, value) in resources do
            values[key] <- value.ToString()

        values

    let private buildResources (compute: KubernetesComputeConfiguration) =
        let resources = compute.ToResourceRequirements()

        {| requests = toResourceDictionary resources.Requests
           limits = toResourceDictionary resources.Limits |}

    let private buildAffinity (compute: KubernetesComputeConfiguration) =
        let affinity = Dictionary<string, obj>()
        let nodeSelector = compute.ToNodeSelector()

        if not (isNull nodeSelector) then
            affinity["nodeSelector"] <- nodeSelector

        let nodeAffinity = compute.ToNodeAffinity()

        if not (isNull nodeAffinity) then
            affinity["nodeAffinity"] <- nodeAffinity

        let tolerations = compute.ToTolerations()

        if not (isNull tolerations) then
            affinity["tolerations"] <- tolerations

        if affinity.Count = 0 then None else Some affinity

    let buildCluster
        (cluster: PostgresCluster)
        (projection: SecretProjectionReference)
        (namespaceName: string)
        (storageClassName: string)
        (compute: KubernetesComputeConfiguration)
        (metadata: PostgresResourceDeploymentMetadata)
        (settings: PostgresSettings)
        =
        cluster.EnsureConfigured()
        let clusterSecretName = projection.KubernetesName

        let image =
            PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version

        let spec = Dictionary<string, obj>()
        spec["instances"] <- cluster.Spec.Instances

        spec["storage"] <-
            {| size = $"{cluster.Spec.Storage}Gi"
               storageClass = storageClassName |}

        spec["resources"] <- buildResources compute

        match buildAffinity compute with
        | Some affinity -> spec["affinity"] <- affinity
        | None -> ()

        spec["enableSuperuserAccess"] <- true
        spec["superuserSecret"] <- {| name = clusterSecretName |}
        spec["imageName"] <- image

        match cluster.Spec.Bootstrap with
        | :? FreshPostgresBootstrap -> ()
        | :? RecoveryPostgresBootstrap as recovery ->
            let origin = recovery.Origin
            let target = Dictionary<string, obj>()
            match origin.Target with
            | :? PostgresTimeRecoveryTarget as time ->
                target["targetTime"] <- time.TargetTimeUtc.ToString("O")
            | :? PostgresCopyRecoveryTarget as copy ->
                target["backupID"] <- copy.BackupId
                target["targetImmediate"] <- true
            | _ -> invalidOp "Unsupported PostgreSQL recovery target."
            spec["bootstrap"] <- {| recovery = {| source = "origin"; recoveryTarget = target |} |}
            spec["externalClusters"] <-
                [| {| name = "origin"
                      plugin =
                        {| name = PostgresBackupNames.PluginName
                           parameters = {| barmanObjectName = PostgresBackupNames.objectStore origin.ArchiveId; serverName = origin.ServerName |} |} |} |]
        | _ -> invalidOp "Unsupported PostgreSQL bootstrap."

        spec["inheritedMetadata"] <-
            {| labels = buildLabels cluster metadata
               annotations = buildAnnotations () |}

        {| apiVersion = "postgresql.cnpg.io/v1"
           kind = "Cluster"
           metadata =
            {| name = buildResourceName cluster.Id
               ``namespace`` = namespaceName
               labels = buildLabels cluster metadata
               annotations = buildAnnotations () |}
           spec = spec |}
