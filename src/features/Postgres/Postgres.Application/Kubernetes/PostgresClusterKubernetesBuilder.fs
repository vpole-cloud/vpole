namespace Pole.Features.Postgres.Application.Kubernetes

open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open k8s.Models

[<RequireQualifiedAccess>]
module PostgresClusterKubernetesBuilder =
    let buildSecretName (clusterId : string) = $"{clusterId}-secret"
    let buildReadWriteServiceName (clusterId : string) = $"{clusterId}-rw"
    let buildDatabaseName (clusterId : string) = clusterId

    let buildLabels (cluster : PostgresCluster) =
        dict [
            "app", cluster.Id
            "component", "postgres"
            PoleStorageLabels.TenantId, cluster.TenantId
            PoleStorageLabels.ResourceGroupId, cluster.GroupId
            PoleStorageLabels.ResourceType, ResourceType.Postgres.ToString()
            PoleStorageLabels.ResourceId, cluster.Id
            PoleStorageLabels.ResourceName, cluster.Name
        ]

    let private buildResources (cluster : PostgresCluster) =
        {| requests =
               {| cpu = $"{cluster.Spec.Resources.Requests.CpuCores}"
                  memory = $"{cluster.Spec.Resources.Requests.MemoryMi}Mi" |}
           limits =
               {| cpu = $"{cluster.Spec.Resources.Limits.CpuCores}"
                  memory = $"{cluster.Spec.Resources.Limits.MemoryMi}Mi" |} |}

    let buildCluster (cluster : PostgresCluster) (username : string) (settings : PostgresSettings) =
        let clusterSecretName = buildSecretName cluster.Id
        let image = PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version

        {|
            apiVersion = "postgresql.cnpg.io/v1"
            kind = "Cluster"
            metadata =
                {|
                    name = cluster.Id
                    ``namespace`` = cluster.Spec.KubernetesNamespace
                    labels = buildLabels cluster
                |}
            spec =
                {|
                    instances = cluster.Spec.Instances
                    storage =
                        {|
                            size = $"{cluster.Spec.Storage}Gi"
                            storageClass = cluster.Spec.StorageProfile.StorageClassName
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

    let buildSecret (cluster : PostgresCluster) (username : string) (password : string) =
        let secret = V1Secret()
        secret.ApiVersion <- "v1"
        secret.Kind <- "Secret"
        secret.Metadata <- V1ObjectMeta(Name = buildSecretName cluster.Id, NamespaceProperty = cluster.Spec.KubernetesNamespace, Labels = buildLabels cluster)
        secret.Type <- "kubernetes.io/basic-auth"
        secret.StringData <- dict [ "username", username; "password", password ]
        secret

