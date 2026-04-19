namespace Pole.Features.Postgres.Kubernetes

open System.Threading
open System.Threading.Tasks
open Pole.Features.Core.Kubernetes
open k8s

[<AbstractClass; Sealed>]
type PostgresClusterKubernetesResourceManager =
    static member CreateAsync(
        kubernetes : IKubernetes,
        cluster : PostgresClusterKubernetesBuilder,
        ?cancellationToken : CancellationToken) =
        let cancellationToken = defaultArg cancellationToken CancellationToken.None

        KubernetesExtensions.CreateIfNotExistAsync(fun () ->
            kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                cluster.BuildCluster(),
                group = "postgresql.cnpg.io",
                version = "v1",
                namespaceParameter = cluster.NamespaceName,
                plural = "clusters",
                cancellationToken = cancellationToken
            )
        )

    static member DeleteAsync(
        kubernetes : IKubernetes,
        clusterId : string,
        namespaceName : string,
        ?cancellationToken : CancellationToken) =
        let cancellationToken = defaultArg cancellationToken CancellationToken.None

        task {
            do!
                KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                    kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        name = clusterId,
                        cancellationToken = cancellationToken
                    )
                )

            do!
                KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                    kubernetes.CoreV1.DeleteNamespacedSecretAsync(
                        name = PostgresClusterKubernetesBuilder.BuildSecretName(clusterId),
                        namespaceParameter = namespaceName,
                        cancellationToken = cancellationToken
                    )
                )
        }
