namespace Pole.Features.Postgres.Application.Kubernetes

open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Secrets.Contracts

type IPostgresClusterDeploymentManager =
    abstract member ApplyAsync:
        kubernetes: IKubernetes *
        cluster: PostgresCluster *
        projection: SecretProjectionReference *
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
        cancellationToken: CancellationToken ->
            Task

[<RequireQualifiedAccess>]
module private PostgresKubernetesApply =
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

    interface IPostgresClusterDeploymentManager with
        member _.ApplyAsync
            (kubernetes, cluster, projection, namespaceName, storageClassName, compute, metadata, cancellationToken)
            =
            task {
                cluster.EnsureConfigured()

                do!
                    PostgresKubernetesApply.customObject
                        kubernetes
                        (PostgresClusterKubernetesBuilder.buildCluster
                            cluster
                            projection
                            namespaceName
                            storageClassName
                            compute
                            metadata
                            settings)
                        (PostgresClusterKubernetesBuilder.buildResourceName cluster.Id)
                        namespaceName
                        cancellationToken
            }

        member _.DeleteAsync(kubernetes, clusterId, namespaceName, cancellationToken) =
            task {
                let kubernetesName = PostgresClusterKubernetesBuilder.buildResourceName clusterId

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

            }
