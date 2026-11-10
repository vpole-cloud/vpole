namespace Pole.Features.Postgres.Application.Kubernetes

open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common

type IPostgresClusterDeploymentManager =
    abstract member ApplyAsync:
        kubernetes: IKubernetes *
        cluster: PostgresCluster *
        credentials: PostgresProjectedCredentials *
        namespaceName: string *
        storageClassName: string *
        compute: KubernetesComputeConfiguration *
        metadata: PostgresResourceDeploymentMetadata *
        cancellationToken: CancellationToken ->
            Task

    abstract member DeleteAsync:
        kubernetes: IKubernetes *
        clusterId: string *
        namespaceName: string *
        deploymentMode: PostgresDeploymentMode *
        cancellationToken: CancellationToken ->
            Task

[<RequireQualifiedAccess>]
module private PostgresKubernetesApply =
    let service (kubernetes: IKubernetes) (manifest: V1Service) namespaceName cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.CoreV1.CreateNamespacedServiceAsync(
                        manifest,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                return ()
            with :? HttpOperationException as ex when
                not (isNull ex.Response) && ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! current =
                    kubernetes.CoreV1.ReadNamespacedServiceAsync(
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                manifest.Metadata.ResourceVersion <- current.Metadata.ResourceVersion
                manifest.Spec.ClusterIP <- current.Spec.ClusterIP
                manifest.Spec.ClusterIPs <- current.Spec.ClusterIPs

                let! _ =
                    kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        manifest,
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                return ()
        }

    let statefulSet (kubernetes: IKubernetes) (manifest: V1StatefulSet) namespaceName cancellationToken =
        task {
            try
                let! _ =
                    kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                        manifest,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                return ()
            with :? HttpOperationException as ex when
                not (isNull ex.Response) && ex.Response.StatusCode = HttpStatusCode.Conflict ->
                let! current =
                    kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                manifest.Metadata.ResourceVersion <- current.Metadata.ResourceVersion
                // Preserve immutable template metadata only when capacity/class are unchanged.
                // Legacy resize requests must still fail instead of reporting unapplied storage.
                let desiredClaim = manifest.Spec.VolumeClaimTemplates.[0]

                if
                    not (isNull current.Spec.VolumeClaimTemplates)
                    && current.Spec.VolumeClaimTemplates.Count = 1
                then
                    let currentClaim = current.Spec.VolumeClaimTemplates.[0]

                    if
                        desiredClaim.Spec.StorageClassName = currentClaim.Spec.StorageClassName
                        && desiredClaim.Spec.Resources.Requests.["storage"].ToString() = currentClaim
                            .Spec.Resources.Requests.["storage"]
                            .ToString()
                    then
                        manifest.Spec.VolumeClaimTemplates <- current.Spec.VolumeClaimTemplates

                let! _ =
                    kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                        manifest,
                        manifest.Metadata.Name,
                        namespaceName,
                        cancellationToken = cancellationToken
                    )

                return ()
        }

    let private isOwnedMetadataKey (key: string) =
        key = "app"
        || key = "component"
        || key.StartsWith("pole.io/", System.StringComparison.Ordinal)
        || key.StartsWith("pole.server/", System.StringComparison.Ordinal)

    let private nativeIdentity (document: JsonObject) =
        let metadata = document["metadata"].AsObject()
        let identity = JsonObject()

        for key in [ "uid"; "generation"; "deletionTimestamp"; "ownerReferences" ] do
            if not (isNull metadata[key]) then
                identity[key] <- metadata[key].DeepClone()

        for kind in [ "labels"; "annotations" ] do
            let owned = JsonObject()

            if not (isNull metadata[kind]) then
                for KeyValue(key, value) in metadata[kind].AsObject() do
                    if isOwnedMetadataKey key then
                        owned[key] <- if isNull value then null else value.DeepClone()

            identity[kind] <- owned

        identity

    let private replacementDocument manifest (current: JsonObject) =
        let replacement = JsonSerializer.SerializeToNode(manifest).AsObject()
        let desiredMetadata = replacement["metadata"].AsObject()
        let currentMetadata = current["metadata"].AsObject()
        // Pole owns its labels/annotations; preserve controller-owned metadata and lifecycle fields.
        for kind in [ "labels"; "annotations" ] do
            let merged = JsonObject()

            if not (isNull currentMetadata[kind]) then
                for KeyValue(key, value) in currentMetadata[kind].AsObject() do
                    if not (isOwnedMetadataKey key) then
                        merged[key] <- if isNull value then null else value.DeepClone()

            if not (isNull desiredMetadata[kind]) then
                for KeyValue(key, value) in desiredMetadata[kind].AsObject() do
                    merged[key] <- if isNull value then null else value.DeepClone()

            desiredMetadata[kind] <- merged

        for key in [ "finalizers"; "ownerReferences" ] do
            if isNull desiredMetadata[key] && not (isNull currentMetadata[key]) then
                desiredMetadata[key] <- currentMetadata[key].DeepClone()

        desiredMetadata["resourceVersion"] <- currentMetadata["resourceVersion"].DeepClone()
        replacement

    let customObject (kubernetes: IKubernetes) manifest clusterId namespaceName (cancellationToken: CancellationToken) =
        let rec replace attempt (baseline: JsonObject option) (conflict: HttpOperationException option) =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                let! existing =
                    kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        name = clusterId,
                        cancellationToken = cancellationToken
                    )

                let current = JsonSerializer.SerializeToNode(existing).AsObject()
                let currentMetadata = current["metadata"].AsObject()
                let uid = currentMetadata["uid"]

                if isNull uid || System.String.IsNullOrWhiteSpace(uid.GetValue<string>()) then
                    invalidOp "PostgreSQL native identity is unavailable."

                match baseline, conflict with
                | Some original, Some error when
                    not (
                        JsonNode.DeepEquals(current["spec"], original["spec"])
                        && JsonNode.DeepEquals(nativeIdentity current, nativeIdentity original)
                    )
                    ->
                    // Rebase only over status/controller changes, never over another writer's intent.
                    ExceptionDispatchInfo.Capture(error).Throw()
                | _ -> ()

                try
                    let! _ =
                        kubernetes.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                            replacementDocument manifest current,
                            group = "postgresql.cnpg.io",
                            version = "v1",
                            namespaceParameter = namespaceName,
                            plural = "clusters",
                            name = clusterId,
                            cancellationToken = cancellationToken
                        )

                    return ()
                with :? HttpOperationException as ex when
                    not (isNull ex.Response)
                    && ex.Response.StatusCode = HttpStatusCode.Conflict
                    && attempt < 3 ->
                    do! Task.Delay(System.TimeSpan.FromMilliseconds(float (50 * attempt)), cancellationToken)
                    return! replace (attempt + 1) (Some(defaultArg baseline current)) (Some ex)
            }

        task {
            try
                let! _ =
                    kubernetes.CustomObjects.CreateNamespacedCustomObjectAsync(
                        manifest,
                        group = "postgresql.cnpg.io",
                        version = "v1",
                        namespaceParameter = namespaceName,
                        plural = "clusters",
                        cancellationToken = cancellationToken
                    )

                return ()
            with :? HttpOperationException as ex when
                not (isNull ex.Response) && ex.Response.StatusCode = HttpStatusCode.Conflict ->
                return! replace 1 None None
        }

type PostgresClusterDeploymentManager(settings: PostgresSettings) =
    let dataPersistentVolumeClaimName clusterId = $"postgres-data-{clusterId}-0"

    interface IPostgresClusterDeploymentManager with
        member _.ApplyAsync
            (kubernetes, cluster, credentials, namespaceName, storageClassName, compute, metadata, cancellationToken)
            =
            task {
                cluster.EnsureConfigured()

                match cluster.Spec.DeploymentMode with
                | PostgresDeploymentMode.Operator ->
                    do!
                        PostgresKubernetesApply.customObject
                            kubernetes
                            (PostgresClusterKubernetesBuilder.buildCluster
                                cluster
                                credentials
                                namespaceName
                                storageClassName
                                compute
                                metadata
                                settings)
                            (PostgresClusterKubernetesBuilder.buildResourceName cluster.Id)
                            namespaceName
                            cancellationToken
                | PostgresDeploymentMode.StatefulSet ->
                    do!
                        PostgresKubernetesApply.service
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildHeadlessService cluster namespaceName metadata)
                            namespaceName
                            cancellationToken

                    do!
                        PostgresKubernetesApply.service
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildReadWriteService cluster namespaceName metadata)
                            namespaceName
                            cancellationToken

                    do!
                        PostgresKubernetesApply.statefulSet
                            kubernetes
                            (PostgresStatefulSetKubernetesBuilder.buildStatefulSet
                                cluster
                                credentials.Projection
                                namespaceName
                                storageClassName
                                compute
                                metadata
                                settings)
                            namespaceName
                            cancellationToken
                | mode -> invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."
            }

        member _.DeleteAsync(kubernetes, clusterId, namespaceName, deploymentMode, cancellationToken) =
            task {
                let kubernetesName = PostgresClusterKubernetesBuilder.buildResourceName clusterId

                match deploymentMode with
                | PostgresDeploymentMode.Operator ->
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                                group = "postgresql.cnpg.io",
                                version = "v1",
                                namespaceParameter = namespaceName,
                                plural = "clusters",
                                name = kubernetesName,
                                cancellationToken = cancellationToken
                            ))
                | PostgresDeploymentMode.StatefulSet ->
                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
                                kubernetesName,
                                namespaceName,
                                cancellationToken = cancellationToken
                            ))

                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                                PostgresClusterKubernetesBuilder.buildReadWriteServiceName clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken
                            ))

                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                                PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName clusterId,
                                namespaceName,
                                cancellationToken = cancellationToken
                            ))

                    do!
                        KubernetesExtensions.DeleteIfExistsAsync(fun () ->
                            kubernetes.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                                dataPersistentVolumeClaimName kubernetesName,
                                namespaceName,
                                cancellationToken = cancellationToken
                            ))
                | mode -> invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."

            }
