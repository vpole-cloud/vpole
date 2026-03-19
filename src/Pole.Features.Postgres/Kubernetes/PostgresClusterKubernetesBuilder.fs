namespace Pole.Features.Postgres.Kubernetes

open Pole.Domain.Models.FSharp
open k8s.Models

type PostgresClusterKubernetesBuilder(
    cluster : PostgresCluster) as this =
    member _.ClusterSecretName = $"{cluster.Id}-secret"
    member _.ClusterInitConfigMapName = $"{cluster.Id}-init"
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
                    postInitApplicationSQLRefs =        
                        {|
                            name = this.ClusterInitConfigMapName
                            key = "init.sql"
                        |}
                |}
        |}
    
    member _.BuildConfigMap() =
        let configMap = V1ConfigMap()
        configMap.ApiVersion <- "v1"
        configMap.Kind <- "ConfigMap"
        configMap.Metadata <- V1ObjectMeta(Name = this.ClusterInitConfigMapName)
        configMap.Data <- dict [ "init.sql", this.BuildInitSql() ]
        configMap
    
    member _.BuildInitSql() =
        let sqlCommands =
            cluster.Databases
            |> List.map (fun db -> $"CREATE DATABASE {db.Name};")
        String.concat "\n" sqlCommands
        
    member _.BuildSecret(password) =
        let secret = V1Secret()
        secret.ApiVersion <- "v1"
        secret.Kind <- "Secret"
        secret.Metadata <- V1ObjectMeta(Name = this.ClusterSecretName)
        secret.StringData <- dict [ "username", cluster.Username; "password", password ]
        secret