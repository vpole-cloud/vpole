namespace Pole.Features.Postgres.Kubernetes

open Pole.Domain.Models.FSharp
open k8s.Models

type PostgresClusterKubernetesBuilder(
    cluster : PostgresCluster,
    username : string) as this =
    member _.NamespaceName = cluster.Spec.KubernetesNamespace
    member _.Username = username
    static member BuildSecretName(clusterId : string) = $"{clusterId}-secret"
    static member BuildReadWriteServiceName(clusterId : string) = $"{clusterId}-rw"
    static member BuildDatabaseName(clusterId : string) = clusterId
    member _.ClusterSecretName = PostgresClusterKubernetesBuilder.BuildSecretName(cluster.Id)
    member _.BuildCluster() =
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
                            name = this.ClusterSecretName
                        |}
                    bootstrap =
                        {|
                            initdb =
                                {|
                                    database = PostgresClusterKubernetesBuilder.BuildDatabaseName(cluster.Id)
                                    owner = username
                                    secret =
                                        {|
                                            name = this.ClusterSecretName
                                        |}
                                |}
                        |}
                |}
        |}
        
    member _.BuildSecret(username, password) =
        let secret = V1Secret()
        secret.ApiVersion <- "v1"
        secret.Kind <- "Secret"
        secret.Metadata <- V1ObjectMeta(Name = this.ClusterSecretName, NamespaceProperty = cluster.Spec.KubernetesNamespace)
        secret.Type <- "kubernetes.io/basic-auth"
        secret.StringData <- dict [ "username", username; "password", password ]
        secret
