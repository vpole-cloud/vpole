namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open Pole.Features.Shared.Models
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceDeployments.Application.Kubernetes
open Pole.Features.ResourceDeployments.Domain
open Pole.Features.StorageProfiles.Contracts
open k8s.Models

type PostgresResourceDeploymentMetadata =
    { DeploymentId : string
      RuntimeCellId : string
      DeploymentTargetId : string
      Generation : int64 }

[<RequireQualifiedAccess>]
module PostgresClusterKubernetesBuilder =
    let buildSecretName (clusterId : string) = $"{clusterId}-secret"
    let buildReadWriteServiceName (clusterId : string) = $"{clusterId}-rw"
    let buildDatabaseName (clusterId : string) = clusterId

    let buildLabels (cluster : PostgresCluster) (metadata : PostgresResourceDeploymentMetadata) =
        dict [
            "app", cluster.Id
            "component", "postgres"
            PoleStorageLabels.TenantId, cluster.TenantId
            PoleStorageLabels.ResourceGroupId, cluster.GroupId
            PoleStorageLabels.ResourceType, ResourceType.Postgres.ToString()
            PoleStorageLabels.ResourceId, cluster.Id
            PoleStorageLabels.ResourceName, cluster.Name
            "pole.io/resource-deployment-id", metadata.DeploymentId
            "pole.io/runtime-cell-id", metadata.RuntimeCellId
            "pole.io/deployment-target-id", metadata.DeploymentTargetId
            "pole.io/deployment-generation", string metadata.Generation
        ]

    let private toResourceDictionary
        (resources : IDictionary<string, ResourceQuantity>) =
        let values = Dictionary<string, string>()
        for KeyValue(key, value) in resources do
            values[key] <- value.ToString()
        values

    let private buildResources (compute : ResolvedComputeConfiguration) =
        let resources =
            ResolvedComputeConfigurationKubernetesExtensions.ToKubernetesResources(compute)
        {| requests = toResourceDictionary resources.Requests
           limits = toResourceDictionary resources.Limits |}

    let private buildAffinity (compute : ResolvedComputeConfiguration) =
        let affinity = Dictionary<string, obj>()
        let nodeSelector =
            ResolvedComputeConfigurationKubernetesExtensions.ToKubernetesNodeSelector(compute)
        if not (isNull nodeSelector) then
            affinity["nodeSelector"] <- nodeSelector
        let nodeAffinity =
            ResolvedComputeConfigurationKubernetesExtensions.ToKubernetesNodeAffinity(compute)
        if not (isNull nodeAffinity) then
            affinity["nodeAffinity"] <- nodeAffinity
        let tolerations =
            ResolvedComputeConfigurationKubernetesExtensions.ToKubernetesTolerations(compute)
        if not (isNull tolerations) then
            affinity["tolerations"] <- tolerations

        if affinity.Count = 0 then None else Some affinity

    let buildCluster
        (cluster : PostgresCluster)
        (username : string)
        (namespaceName : string)
        (storageClassName : string)
        (compute : ResolvedComputeConfiguration)
        (metadata : PostgresResourceDeploymentMetadata)
        (settings : PostgresSettings) =
        let clusterSecretName = buildSecretName cluster.Id
        let image = PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version
        let spec = Dictionary<string, obj>()
        spec["instances"] <- cluster.Spec.Instances
        spec["storage"] <-
            {|
                size = $"{cluster.Spec.Storage}Gi"
                storageClass = storageClassName
            |}
        spec["resources"] <- buildResources compute
        match buildAffinity compute with
        | Some affinity -> spec["affinity"] <- affinity
        | None -> ()
        spec["secret"] <- {| name = clusterSecretName |}
        spec["imageName"] <- image
        spec["bootstrap"] <-
            {|
                initdb =
                    {|
                        database = buildDatabaseName cluster.Id
                        owner = username
                        secret = {| name = clusterSecretName |}
                    |}
            |}

        {|
            apiVersion = "postgresql.cnpg.io/v1"
            kind = "Cluster"
            metadata =
                {|
                    name = cluster.Id
                    ``namespace`` = namespaceName
                    labels = buildLabels cluster metadata
                |}
            spec = spec
        |}

    let buildSecret
        (cluster : PostgresCluster)
        (credentials : PostgresClusterCredentials)
        (namespaceName : string)
        (metadata : PostgresResourceDeploymentMetadata) =
        let secret = V1Secret()
        secret.ApiVersion <- "v1"
        secret.Kind <- "Secret"
        secret.Metadata <-
            V1ObjectMeta(
                Name = buildSecretName cluster.Id,
                NamespaceProperty = namespaceName,
                Labels = buildLabels cluster metadata)
        secret.Type <- "kubernetes.io/basic-auth"
        secret.StringData <-
            dict [
                "username", credentials.Username
                "password", credentials.Password
            ]
        secret
