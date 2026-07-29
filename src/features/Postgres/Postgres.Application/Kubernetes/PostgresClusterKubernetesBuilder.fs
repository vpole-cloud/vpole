namespace Pole.Features.Postgres.Application.Kubernetes

open Pole.Features.Shared.Models
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
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

    let private buildResources (cluster : PostgresCluster) =
        {| requests =
               {| cpu = $"{cluster.Spec.Resources.Requests.CpuCores}"
                  memory = $"{cluster.Spec.Resources.Requests.MemoryMi}Mi" |}
           limits =
               {| cpu = $"{cluster.Spec.Resources.Limits.CpuCores}"
                  memory = $"{cluster.Spec.Resources.Limits.MemoryMi}Mi" |} |}

    let buildCluster
        (cluster : PostgresCluster)
        (username : string)
        (namespaceName : string)
        (storageClassName : string)
        (metadata : PostgresResourceDeploymentMetadata)
        (settings : PostgresSettings) =
        let clusterSecretName = buildSecretName cluster.Id
        let image = PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version

        {|
            apiVersion = "postgresql.cnpg.io/v1"
            kind = "Cluster"
            metadata =
                {|
                    name = cluster.Id
                    ``namespace`` = namespaceName
                    labels = buildLabels cluster metadata
                |}
            spec =
                {|
                    instances = cluster.Spec.Instances
                    storage =
                        {|
                            size = $"{cluster.Spec.Storage}Gi"
                            storageClass = storageClassName
                        |}
                    resources = buildResources cluster
                    secret =
                        {|
                            name = clusterSecretName
                        |}
                    imageName = image
                    bootstrap =
                        {|
                            initdb =
                                {|
                                    database = buildDatabaseName cluster.Id
                                    owner = username
                                    secret =
                                        {|
                                            name = clusterSecretName
                                        |}
                                |}
                        |}
                |}
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
