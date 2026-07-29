namespace Pole.Features.Postgres.Application.Kubernetes

open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common

type IPostgresClusterDeploymentManager =
    abstract member ApplyAsync :
        kubernetes : IKubernetes *
        cluster : PostgresCluster *
        credentials : PostgresClusterCredentials *
        namespaceName : string *
        storageClassName : string *
        metadata : PostgresResourceDeploymentMetadata *
        cancellationToken : CancellationToken -> Task

    abstract member DeleteAsync :
        kubernetes : IKubernetes *
        clusterId : string *
        namespaceName : string *
        deploymentMode : PostgresDeploymentMode *
        cancellationToken : CancellationToken -> Task

[<RequireQualifiedAccess>]
module private PostgresKubernetesApply =
    let secret
        (kubernetes : IKubernetes)
        (manifest : V1Secret)
        namespaceName
        cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.CoreV1.CreateNamespacedSecretAsync(
                        manifest,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
            with
            | :? HttpOperationException as ex
                when not (isNull ex.Response) &&
                     ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! current =
                    kubernetes.CoreV1.ReadNamespacedSecretAsync(
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                manifest.Metadata.ResourceVersion <- current.Metadata.ResourceVersion
                let! _ =
                    kubernetes.CoreV1.ReplaceNamespacedSecretAsync(
                        manifest,
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
        }

    let service
        (kubernetes : IKubernetes)
        (manifest : V1Service)
        namespaceName
        cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.CoreV1.CreateNamespacedServiceAsync(
                        manifest,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
            with
            | :? HttpOperationException as ex
                when not (isNull ex.Response) &&
                     ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! current =
                    kubernetes.CoreV1.ReadNamespacedServiceAsync(
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                manifest.Metadata.ResourceVersion <- current.Metadata.ResourceVersion
                manifest.Spec.ClusterIP <- current.Spec.ClusterIP
                manifest.Spec.ClusterIPs <- current.Spec.ClusterIPs
                let! _ =
                    kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        manifest,
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
        }

    let statefulSet
        (kubernetes : IKubernetes)
        (manifest : V1StatefulSet)
        namespaceName
        cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                        manifest,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
            with
            | :? HttpOperationException as ex
                when not (isNull ex.Response) &&
                     ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! current =
                    kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                manifest.Metadata.ResourceVersion <- current.Metadata.ResourceVersion
                let! _ =
                    kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                        manifest,
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken)
                return ()
        }

    let customObject
        (kubernetes : IKubernetes)
        manifest
        clusterId
        namespaceName
        cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                        manifest,
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        cancellationToken = cancellationToken)
                return ()
            with
            | :? HttpOperationException as ex
                when not (isNull ex.Response) &&
                     ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! existing =
                    kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        name = clusterId,
                        cancellationToken = cancellationToken)

                let existingDocument = JsonSerializer.SerializeToNode(existing).AsObject()
                let resourceVersion =
                    ((existingDocument["metadata"].AsObject())["resourceVersion"])
                        .GetValue<string>()
                let replacement = JsonSerializer.SerializeToNode(manifest).AsObject()
                (replacement["metadata"].AsObject())["resourceVersion"] <- resourceVersion

                let! _ =
                    kubernetes.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        replacement,
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        name = clusterId,
                        cancellationToken = cancellationToken)
                return ()
        }

type PostgresClusterDeploymentManager(settings : PostgresSettings) =
    let dataPersistentVolumeClaimName clusterId =
        $"postgres-data-{clusterId}-0"

    let deleteSecret (kubernetes : IKubernetes) clusterId namespaceName cancellationToken =
        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
            kubernetes.CoreV1.DeleteNamespacedSecretAsync(
                PostgresClusterKubernetesBuilder.buildSecretName clusterId,
                namespaceName,
                cancellationToken = cancellationToken)
            :> Task)

    interface IPostgresClusterDeploymentManager with
        member _.ApplyAsync(
            kubernetes,
            cluster,
            credentials,
            namespaceName,
            storageClassName,
            metadata,
            cancellationToken) =
            task {
                do!
                    PostgresKubernetesApply.secret
                        kubernetes
                        (PostgresClusterKubernetesBuilder.buildSecret
                            cluster
                            credentials
                            namespaceName
                            metadata)
                        namespaceName
                        cancellationToken

                match cluster.Spec.DeploymentMode with
                | PostgresDeploymentMode.Operator ->
                    do!
                        PostgresKubernetesApply.customObject
                            kubernetes
                            (PostgresClusterKubernetesBuilder.buildCluster
                                cluster
                                credentials.Username
                                namespaceName
                                storageClassName
                                metadata
                                settings)
                            cluster.Id
                            namespaceName
                            cancellationToken
                | PostgresDeploymentMode.StatefulSet ->
                    do!
                        PostgresKubernetesApply.service
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildHeadlessService
                                cluster
                                namespaceName
                                metadata)
                            namespaceName
                            cancellationToken
                    do!
                        PostgresKubernetesApply.service
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildReadWriteService
                                cluster
                                namespaceName
                                metadata)
                            namespaceName
                            cancellationToken
                    do!
                        PostgresKubernetesApply.statefulSet
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildStatefulSet
                                cluster
                                namespaceName
                                storageClassName
                                metadata
                                settings)
                            namespaceName
                            cancellationToken
                | mode ->
                    invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."
            }

        member _.DeleteAsync(
            kubernetes,
            clusterId,
            namespaceName,
            deploymentMode,
            cancellationToken) =
            task {
                match deploymentMode with
                | PostgresDeploymentMode.Operator ->
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                                group = "postgresql.cnpg.io",
                                version = "v1",
                                namespaceParameter = namespaceName,
                                plural = "clusters",
                                name = clusterId,
                                cancellationToken = cancellationToken))
                | PostgresDeploymentMode.StatefulSet ->
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
                                clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken))
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                                PostgresClusterKubernetesBuilder.buildReadWriteServiceName clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken))
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                                PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken))
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                                dataPersistentVolumeClaimName clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken))
                | mode ->
                    invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."

                do! deleteSecret kubernetes clusterId namespaceName cancellationToken
            }
