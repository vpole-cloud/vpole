namespace Pole.Features.Postgres.Kubernetes

open Pole.Domain.Models.FSharp
open k8s.Models

type PostgresClusterKubernetesBuilder(
    cluster : PostgresCluster) as this =
    member _.ClusterSecretName = $"{cluster.Id}-secret"
    member _.BuildCluster() =
        {|
            apiVersion = "postgresql.cnpg.io/v1"
            kind = "Cluster"
            metadata =
                {|
                    name = cluster.Id
                    ``namespace`` = cluster.GroupId
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
                |}
        |}
        
    member _.BuildSecret(password) =
        let secret = V1Secret()
        secret.ApiVersion <- "v1"
        secret.Kind <- "Secret"
        secret.Metadata <- V1ObjectMeta(Name = this.ClusterSecretName)
        secret.StringData <- dict [ "username", cluster.Username; "password", password ]
        secret
