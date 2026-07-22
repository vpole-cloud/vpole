namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open k8s
open Pole.Features.Postgres.Domain
open Pole.Features.Core.Kubernetes
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common

type IPostgresClusterDeploymentManager =
    abstract member CreateAsync : cluster : PostgresCluster * username : string * cancellationToken : CancellationToken -> Task
    abstract member UpdateAsync : cluster : PostgresCluster * username : string * cancellationToken : CancellationToken -> Task
    abstract member DeleteAsync : clusterId : string * namespaceName : string * deploymentMode : PostgresDeploymentMode * cancellationToken : CancellationToken -> Task

type IPostgresClusterDeploymentBackend =
    abstract member DeploymentMode : PostgresDeploymentMode
    abstract member CreateAsync : cluster : PostgresCluster * username : string * cancellationToken : CancellationToken -> Task
    abstract member UpdateAsync : cluster : PostgresCluster * username : string * cancellationToken : CancellationToken -> Task
    abstract member DeleteAsync : clusterId : string * namespaceName : string * cancellationToken : CancellationToken -> Task

type OperatorPostgresClusterDeploymentBackend(
    kubernetesClientProvider : IKubernetesClientProvider,
    settings : PostgresSettings) =
    interface IPostgresClusterDeploymentBackend with
        member _.DeploymentMode = PostgresDeploymentMode.Operator

        member _.CreateAsync(cluster, username, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                do!
                    KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                        kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                            PostgresClusterKubernetesBuilder.buildCluster cluster username settings,
                            group = "postgresql.cnpg.io",
                            version = "v1",
                            namespaceParameter = cluster.Spec.KubernetesNamespace,
                            plural = "clusters",
                            cancellationToken = cancellationToken)
                        :> Task)
            }

        member _.UpdateAsync(cluster, username, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                let! existingResource =
                    kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = cluster.Spec.KubernetesNamespace,
                        plural = "clusters",
                        name = cluster.Id,
                        cancellationToken = cancellationToken)

                let existingDocument = System.Text.Json.JsonSerializer.SerializeToNode(existingResource).AsObject()
                let existingMetadata = existingDocument["metadata"].AsObject()
                let resourceVersion = existingMetadata["resourceVersion"].GetValue<string>()

                let updatedResource =
                    System.Text.Json.JsonSerializer.SerializeToNode(
                        PostgresClusterKubernetesBuilder.buildCluster cluster username settings).AsObject()
                let updatedMetadata = updatedResource["metadata"].AsObject()
                updatedMetadata["resourceVersion"] <- resourceVersion

                do!
                    kubernetes.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        updatedResource,
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = cluster.Spec.KubernetesNamespace,
                        plural = "clusters",
                        name = cluster.Id,
                        cancellationToken = cancellationToken)
                    :> Task
            }

        member _.DeleteAsync(clusterId, namespaceName, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                            group = "postgresql.cnpg.io",
                            version = "v1",
                            namespaceParameter = namespaceName,
                            plural = "clusters",
                            name = clusterId,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CoreV1.DeleteNamespacedSecretAsync(
                            name = PostgresClusterKubernetesBuilder.buildSecretName clusterId,
                            namespaceParameter = namespaceName,
                            cancellationToken = cancellationToken))
            }

type StatefulSetPostgresClusterDeploymentBackend(
    kubernetesClientProvider : IKubernetesClientProvider,
    settings : PostgresSettings) =
    let dataPersistentVolumeClaimName clusterId =
        $"postgres-data-{clusterId}-0"

    interface IPostgresClusterDeploymentBackend with
        member _.DeploymentMode = PostgresDeploymentMode.StatefulSet

        member _.CreateAsync(cluster, _, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                do!
                    KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                        kubernetes.CoreV1.CreateNamespacedServiceAsync(
                            PostgresStatefulSetKubernetesBuilder.buildHeadlessService cluster,
                            cluster.Spec.KubernetesNamespace,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                        kubernetes.CoreV1.CreateNamespacedServiceAsync(
                            PostgresStatefulSetKubernetesBuilder.buildReadWriteService cluster,
                            cluster.Spec.KubernetesNamespace,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                        kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                            PostgresStatefulSetKubernetesBuilder.buildStatefulSet cluster settings,
                            cluster.Spec.KubernetesNamespace,
                            cancellationToken = cancellationToken))

                do!
                    kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                        PostgresStatefulSetKubernetesBuilder.buildStatefulSet cluster settings,
                        cluster.Id,
                        cluster.Spec.KubernetesNamespace,
                        cancellationToken = cancellationToken)
                    :> Task
            }

        member _.UpdateAsync(cluster, _, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                do!
                    kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        PostgresStatefulSetKubernetesBuilder.buildReadWriteService cluster,
                        PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id,
                        cluster.Spec.KubernetesNamespace,
                        cancellationToken = cancellationToken)
                    :> Task

                do!
                    kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        PostgresStatefulSetKubernetesBuilder.buildHeadlessService cluster,
                        PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName cluster.Id,
                        cluster.Spec.KubernetesNamespace,
                        cancellationToken = cancellationToken)
                    :> Task

                do!
                    kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                        PostgresStatefulSetKubernetesBuilder.buildStatefulSet cluster settings,
                        cluster.Id,
                        cluster.Spec.KubernetesNamespace,
                        cancellationToken = cancellationToken)
                    :> Task
            }

        member _.DeleteAsync(clusterId, namespaceName, cancellationToken) =
            let headlessServiceName = PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName clusterId
            let readWriteServiceName = PostgresClusterKubernetesBuilder.buildReadWriteServiceName clusterId

            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
                            clusterId,
                            namespaceName,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                            readWriteServiceName,
                            namespaceName,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                            headlessServiceName,
                            namespaceName,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                            dataPersistentVolumeClaimName clusterId,
                            namespaceName,
                            cancellationToken = cancellationToken))

                do!
                    KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                        kubernetes.CoreV1.DeleteNamespacedSecretAsync(
                            name = PostgresClusterKubernetesBuilder.buildSecretName clusterId,
                            namespaceParameter = namespaceName,
                            cancellationToken = cancellationToken))
            }

type PostgresClusterDeploymentManager(backends : IEnumerable<IPostgresClusterDeploymentBackend>) =
    let backendByMode =
        backends
        |> Seq.map (fun backend -> backend.DeploymentMode, backend)
        |> dict

    let getBackend mode =
        match backendByMode.TryGetValue mode with
        | true, backend -> backend
        | false, _ -> failwith $"Postgres deployment backend '{mode}' is not registered."

    interface IPostgresClusterDeploymentManager with
        member _.CreateAsync(cluster, username, cancellationToken) =
            (getBackend cluster.Spec.DeploymentMode).CreateAsync(cluster, username, cancellationToken)

        member _.UpdateAsync(cluster, username, cancellationToken) =
            (getBackend cluster.Spec.DeploymentMode).UpdateAsync(cluster, username, cancellationToken)

        member _.DeleteAsync(clusterId, namespaceName, deploymentMode, cancellationToken) =
            (getBackend deploymentMode).DeleteAsync(clusterId, namespaceName, cancellationToken)

