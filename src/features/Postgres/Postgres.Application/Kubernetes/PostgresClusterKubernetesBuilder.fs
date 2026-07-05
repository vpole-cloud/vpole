namespace Pole.Features.Postgres.Application.Kubernetes

open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open k8s.Models

[<RequireQualifiedAccess>]
module PostgresClusterKubernetesBuilder =
    let buildSecretName (clusterId : string) = $"{clusterId}-secret"
    let buildReadWriteServiceName (clusterId : string) = $"{clusterId}-rw"
    let buildDatabaseName (clusterId : string) = clusterId

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
                |}
            spec =
                {|
                    instances = cluster.Spec.Instances
                    storage =
                        {|
                            size = $"{cluster.Spec.Storage}Gi"
                        |}
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
        secret.Metadata <- V1ObjectMeta(Name = buildSecretName cluster.Id, NamespaceProperty = cluster.Spec.KubernetesNamespace)
        secret.Type <- "kubernetes.io/basic-auth"
        secret.StringData <- dict [ "username", username; "password", password ]
        secret

