namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open k8s
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

/// CNPG does not report condition observedGeneration. Freshness requires actual owned instance intent, not inherited labels alone.
module internal PostgresOperatorReadiness =
    let private deserialize<'T> value =
        match value with
        | Some(value: JsonElement) -> KubernetesJson.Deserialize<'T>(value.GetRawText())
        | _ -> Unchecked.defaultof<'T>

    let private normalized (value: obj) =
        match value with
        | null -> "null"
        | :? System.Collections.ICollection as values when values.Count = 0 -> "null"
        | _ -> KubernetesJson.Serialize(value)

    let private same left right = normalized left = normalized right

    let private quantities
        (expected: IDictionary<string, ResourceQuantity>)
        (actual: IDictionary<string, ResourceQuantity>)
        =
        not (isNull actual)
        && expected.Count = actual.Count
        && (expected
            |> Seq.forall (fun (KeyValue(key, value)) ->
                actual.ContainsKey(key) && not (isNull actual[key]) && value.Equals(actual[key])))

    let private resources (expected: V1ResourceRequirements) (actual: V1ResourceRequirements) =
        not (isNull actual)
        && quantities expected.Requests actual.Requests
        && quantities expected.Limits actual.Limits

    let private selectors (expected: IDictionary<string, string>) (actual: IDictionary<string, string>) =
        let values value =
            if isNull value then
                [||]
            else
                value
                |> Seq.map (fun (KeyValue(key, item)) -> key, item)
                |> Seq.sort
                |> Seq.toArray

        values expected = values actual

    let private tolerations (expected: IList<V1Toleration>) (actual: IList<V1Toleration>) =
        let desired =
            if isNull expected then
                [||]
            else
                expected |> Seq.map normalized |> Seq.toArray

        let actual = if isNull actual then [||] else actual |> Seq.toArray

        (desired
         |> Array.forall (fun value -> actual |> Array.exists (fun item -> normalized item = value)))
        && (actual
            |> Array.forall (fun item ->
                (desired |> Array.contains (normalized item))
                || (item.Key = "node.kubernetes.io/not-ready"
                    || item.Key = "node.kubernetes.io/unreachable")
                   && item.OperatorProperty = "Exists"
                   && item.Effect = "NoExecute"
                   && item.TolerationSeconds = Nullable 300L))

    let matchesAsync
        (observed: PostgresClusterObservation)
        (cluster: PostgresCluster)
        (deployment: ResourceDeploymentReconciliationSnapshot)
        (settings: PostgresSettings)
        (client: IKubernetes)
        (ct: CancellationToken)
        =
        task {
            try
                let spec = observed.Spec
                let prop = PostgresClusterObservation.property
                let text = PostgresClusterObservation.stringValue

                let compute =
                    PostgresResourceDeploymentMappings.kubernetesCompute deployment.Placement.Compute

                let expectedResources = compute.ToResourceRequirements()
                let actualResources = deserialize<V1ResourceRequirements> (prop spec "resources")
                let affinity = prop spec "affinity"

                let affinityProp key =
                    affinity |> Option.bind (fun value -> prop value key)

                let nodeSelector =
                    deserialize<Dictionary<string, string>> (affinityProp "nodeSelector")

                let nodeAffinity = deserialize<V1NodeAffinity> (affinityProp "nodeAffinity")

                let desiredTolerations =
                    deserialize<ResizeArray<V1Toleration>> (affinityProp "tolerations")

                let storage = prop spec "storage"

                let storageProp key =
                    storage |> Option.bind (fun value -> prop value key)

                let image =
                    PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version

                let expectedStorage = ResourceQuantity($"{cluster.Spec.Storage}Gi")
                let ns = deployment.Placement.KubernetesNamespace

                let identity =
                    WorkloadIdentityLabels(
                        deployment.TenantId,
                        deployment.ResourceGroupId,
                        deployment.IsolationScopeId,
                        deployment.RuntimeCellId,
                        deployment.Placement.DeploymentTargetId,
                        "Postgres",
                        deployment.ResourceId,
                        deployment.Id
                    )

                let controller = observed.Metadata

                let intentCurrent =
                    observed.Desired = cluster.Spec.Instances
                    && text (prop spec "imageName") = image
                    && resources expectedResources actualResources
                    && selectors (compute.ToNodeSelector()) nodeSelector
                    && same (compute.ToNodeAffinity()) nodeAffinity
                    && same (compute.ToTolerations()) desiredTolerations
                    && not (isNull deployment.Placement.Storage)
                    && text (storageProp "storageClass") = deployment.Placement.Storage.StorageClassName
                    && expectedStorage.Equals(ResourceQuantity(text (storageProp "size")))
                    && controller.Name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
                    && controller.NamespaceProperty = ns
                    && KubernetesWorkloadObservation.Owns(controller, identity)
                    && KubernetesWorkloadObservation.Generation(controller, "pole.io/deployment-generation", true) = deployment.DesiredGeneration

                if not intentCurrent then
                    return false
                else
                    let selector =
                        identity.ToDictionary()
                        |> Seq.map (fun (KeyValue(key, value)) -> $"{key}={value}")
                        |> String.concat ","

                    let! listed =
                        client.CoreV1.ListNamespacedPodAsync(
                            ns,
                            labelSelector = selector,
                            limit = Nullable 100,
                            cancellationToken = ct
                        )

                    let pods =
                        listed.Items
                        |> Seq.filter (fun pod ->
                            pod.Metadata.NamespaceProperty = ns
                            && KubernetesWorkloadObservation.Owns(pod.Metadata, identity)
                            && KubernetesOwnedWatchItem.ControlledBy(
                                pod.Metadata,
                                controller.Uid,
                                "Cluster",
                                "postgresql.cnpg.io/v1"
                            )
                            && not pod.Metadata.DeletionTimestamp.HasValue)
                        |> Seq.toArray

                    let mutable current =
                        String.IsNullOrEmpty(listed.Metadata.ContinueProperty)
                        && pods.Length = observed.Desired

                    for pod in pods do
                        let container =
                            if isNull pod.Spec || isNull pod.Spec.Containers then
                                None
                            else
                                pod.Spec.Containers |> Seq.tryFind (fun item -> item.Name = "postgres")

                        match container with
                        | None -> current <- false
                        | Some container ->
                            current <-
                                current
                                && container.Image = image
                                && resources expectedResources container.Resources
                                && selectors nodeSelector pod.Spec.NodeSelector
                                && same
                                    nodeAffinity
                                    (if isNull pod.Spec.Affinity then
                                         null
                                     else
                                         pod.Spec.Affinity.NodeAffinity)
                                && tolerations desiredTolerations pod.Spec.Tolerations
                                && KubernetesWorkloadObservation.Generation(
                                    pod.Metadata,
                                    "pole.io/deployment-generation",
                                    true
                                ) = deployment.DesiredGeneration
                                && KubernetesWorkloadObservation
                                    .SummarizePod(
                                        pod,
                                        deployment.DesiredGeneration,
                                        "pole.io/deployment-generation",
                                        true
                                    )
                                    .Ready

                            let mounts =
                                if isNull container.VolumeMounts then
                                    [||]
                                else
                                    container.VolumeMounts
                                    |> Seq.filter (fun item -> item.MountPath = "/var/lib/postgresql/data")
                                    |> Seq.toArray

                            let volume =
                                if mounts.Length <> 1 || isNull pod.Spec.Volumes then
                                    None
                                else
                                    pod.Spec.Volumes |> Seq.tryFind (fun volume -> volume.Name = mounts[0].Name)

                            match volume with
                            | Some volume when
                                not (isNull volume.PersistentVolumeClaim)
                                && not (String.IsNullOrWhiteSpace(volume.PersistentVolumeClaim.ClaimName))
                                ->
                                let! pvc =
                                    client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                                        volume.PersistentVolumeClaim.ClaimName,
                                        ns,
                                        cancellationToken = ct
                                    )

                                current <-
                                    current
                                    && pvc.Metadata.NamespaceProperty = ns
                                    && KubernetesWorkloadObservation.Owns(pvc.Metadata, identity)
                                    && KubernetesOwnedWatchItem.ControlledBy(
                                        pvc.Metadata,
                                        controller.Uid,
                                        "Cluster",
                                        "postgresql.cnpg.io/v1"
                                    )
                                    && not (isNull pvc.Spec)
                                    && pvc.Spec.StorageClassName = deployment.Placement.Storage.StorageClassName
                                    && not (isNull pvc.Spec.Resources)
                                    && not (isNull pvc.Spec.Resources.Requests)
                                    && pvc.Spec.Resources.Requests.ContainsKey("storage")
                                    && expectedStorage.Equals(pvc.Spec.Resources.Requests["storage"])
                                    && not (isNull pvc.Status)
                                    && pvc.Status.Phase = "Bound"
                                    && not (isNull pvc.Status.Capacity)
                                    && pvc.Status.Capacity.ContainsKey("storage")
                                    && pvc.Status.Capacity["storage"].ToDecimal() >= expectedStorage.ToDecimal()
                            | _ -> current <- false
                    // Fence both the native generation and complete spec after these cross-kind reads.
                    let! after =
                        client.CustomObjects.GetNamespacedCustomObjectAsync(
                            "postgresql.cnpg.io",
                            "v1",
                            ns,
                            "clusters",
                            controller.Name,
                            cancellationToken = ct
                        )

                    match PostgresClusterObservation.tryRead after with
                    | Some after ->
                        return
                            current
                            && after.Metadata.Uid = controller.Uid
                            && after.Metadata.Generation = controller.Generation
                            && after.Metadata.NamespaceProperty = ns
                            && KubernetesWorkloadObservation.Owns(after.Metadata, identity)
                            && after.Spec.GetRawText() = observed.Spec.GetRawText()
                            && (after.ReadyCondition || after.RequiresPodConformance)
                    | None -> return false
            with
            | :? JsonException
            | :? ArgumentException
            | :? FormatException
            | :? OverflowException -> return false
        }
