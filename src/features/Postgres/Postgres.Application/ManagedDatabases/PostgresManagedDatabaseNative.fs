namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Globalization
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open k8s
open k8s.Autorest
open k8s.Models
open Pole.Features.Postgres.Domain
open Pole.Features.Core.Kubernetes

/// Kubernetes operations retain native UID/resourceVersion fences and refuse foreign ownership.
type PostgresManagedDatabaseNative() =
    let metadata (document: JsonObject) =
        KubernetesJson.Deserialize<V1ObjectMeta>(document["metadata"].ToJsonString())

    let revision (value: V1ObjectMeta) =
        if isNull value.Annotations then 0L
        else match value.Annotations.TryGetValue PostgresManagedDatabaseManifests.RevisionAnnotation with
             | true, text -> match Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with true, number -> number | _ -> Int64.MaxValue
             | _ -> 0L

    let json (value: obj) = JsonSerializer.SerializeToNode(value).AsObject()
    let field (value: JsonNode) (name: string) = if isNull value then null else value[name]
    let text value name = let item = field value name in if isNull item then "" else item.ToString()
    let matches (claim: PostgresManagedDatabase) (value: V1ObjectMeta) =
        PostgresManagedDatabaseManifests.owns claim value && revision value <= claim.Revision &&
        not (String.IsNullOrWhiteSpace value.Uid) && not (String.IsNullOrWhiteSpace value.ResourceVersion)

    member _.ReadCustomAsync(client: IKubernetes, claim: PostgresManagedDatabase, plural: string, ct: CancellationToken) =
        task {
            try
                let! value = client.CustomObjects.GetNamespacedCustomObjectAsync(
                                "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, plural,
                                PostgresManagedDatabaseManifests.resourceName claim, cancellationToken = ct)
                return Some(json value)
            with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound ->
                return None
        }

    member this.ApplyCustomAsync(client: IKubernetes, claim: PostgresManagedDatabase, plural: string,
                                 desired: obj, isCurrent: CancellationToken -> System.Threading.Tasks.Task<bool>, ct: CancellationToken) =
        task {
            let! current = this.ReadCustomAsync(client, claim, plural, ct)
            let wanted = json desired
            let! authorized = isCurrent ct
            if not authorized then return false
            else
                try
                    match current with
                    | None ->
                        let! _ = client.CustomObjects.CreateNamespacedCustomObjectAsync(
                                    desired, "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, plural, cancellationToken = ct)
                        return true
                    | Some document ->
                        let identity = metadata document
                        if not (matches claim identity) || identity.DeletionTimestamp.HasValue then return false
                        elif JsonNode.DeepEquals(document["spec"], wanted["spec"]) && revision identity = claim.Revision then return true
                        else
                            for key in [ "resourceVersion"; "uid"; "finalizers"; "ownerReferences" ] do
                                if not (isNull (document["metadata"][key])) then wanted["metadata"][key] <- (document["metadata"][key]).DeepClone()
                            let! _ = client.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                                        wanted, "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, plural,
                                        PostgresManagedDatabaseManifests.resourceName claim, cancellationToken = ct)
                            return true
                with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict ->
                    return false
        }

    member this.DeleteCustomAsync(client: IKubernetes, claim: PostgresManagedDatabase, plural: string, ct: CancellationToken) =
        task {
            let! current = this.ReadCustomAsync(client, claim, plural, ct)
            match current with
            | None -> return true
            | Some document ->
                let identity = metadata document
                if not (matches claim identity) then return false
                elif identity.DeletionTimestamp.HasValue then return false
                else
                    try
                        let! _ = client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                                    "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, plural,
                                    PostgresManagedDatabaseManifests.resourceName claim,
                                    body = V1DeleteOptions(Preconditions = V1Preconditions(Uid = identity.Uid, ResourceVersion = identity.ResourceVersion)),
                                    cancellationToken = ct)
                        return false // Finalizer completion is confirmed by a later authoritative read.
                    with
                    | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return true
                    | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict -> return false
        }

    member _.ApplyPolicyAsync(client: IKubernetes, claim: PostgresManagedDatabase, desired: V1NetworkPolicy,
                              isCurrent: CancellationToken -> System.Threading.Tasks.Task<bool>, ct: CancellationToken) =
        task {
            let! current =
                task {
                    try
                        let! policy = client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(desired.Metadata.Name, desired.Metadata.NamespaceProperty, cancellationToken = ct)
                        return Some policy
                    with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return None
                }
            let! authorized = isCurrent ct
            if not authorized then return false
            else
                try
                    match current with
                    | None ->
                        let! _ = client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(desired, desired.Metadata.NamespaceProperty, cancellationToken = ct)
                        return true
                    | Some policy when not (matches claim policy.Metadata) || policy.Metadata.DeletionTimestamp.HasValue -> return false
                    | Some policy when KubernetesJson.Serialize(policy.Spec) = KubernetesJson.Serialize(desired.Spec) && revision policy.Metadata = claim.Revision -> return true
                    | Some policy ->
                        desired.Metadata.ResourceVersion <- policy.Metadata.ResourceVersion
                        desired.Metadata.Uid <- policy.Metadata.Uid
                        desired.Metadata.Finalizers <- policy.Metadata.Finalizers
                        let! _ = client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(desired, desired.Metadata.Name, desired.Metadata.NamespaceProperty, cancellationToken = ct)
                        return true
                with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict -> return false
        }

    member _.DeletePolicyAsync(client: IKubernetes, claim: PostgresManagedDatabase, policyName: string, ct: CancellationToken) =
        task {
            try
                let! current = client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                                policyName, claim.Target.KubernetesNamespace, cancellationToken = ct)
                if not (matches claim current.Metadata) then return false
                else
                    let! _ = client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                                current.Metadata.Name, current.Metadata.NamespaceProperty,
                                body = V1DeleteOptions(Preconditions = V1Preconditions(Uid = current.Metadata.Uid, ResourceVersion = current.Metadata.ResourceVersion)), cancellationToken = ct)
                    return true
            with
            | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return true
            | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict -> return false
        }

    member _.Ready(claim: PostgresManagedDatabase, document: JsonObject, desired: obj, secretVersion: string option) =
        let status = document["status"]
        let meta = document["metadata"]
        let identity = metadata document
        matches claim identity && not identity.DeletionTimestamp.HasValue &&
        identity.Generation.GetValueOrDefault() > 0L && revision identity = claim.Revision &&
        JsonNode.DeepEquals(document["spec"], (json desired)["spec"]) &&
        text status "applied" = "true" && text status "observedGeneration" = text meta "generation" &&
        (secretVersion |> Option.forall(fun version -> text status "secretResourceVersion" = version))

    member _.ReadReadyClusterUidAsync(client: IKubernetes, claim: PostgresManagedDatabase, cluster: PostgresCluster, ct: CancellationToken) =
        task {
            try
                let! raw = client.CustomObjects.GetNamespacedCustomObjectAsync(
                                "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, "clusters",
                                PostgresManagedDatabaseManifests.clusterName claim, cancellationToken = ct)
                let value = json raw
                let meta = metadata value
                let labels = meta.Labels
                let label (name: string) expected = not (isNull labels) && match labels.TryGetValue name with true, actual -> actual = expected | _ -> false
                let spec = value["spec"]
                let synchronous = field (field spec "postgresql") "synchronous"
                let affinity = field spec "affinity"
                let status = value["status"]
                if cluster.Spec.ReplicationMode <> PostgresReplicationMode.SynchronousHa || cluster.Spec.Instances < 3 ||
                   not (label "pole.io/tenant-id" claim.TenantId && label "pole.io/runtime-cell-id" claim.Target.RuntimeCellId &&
                        label "pole.io/resource-deployment-id" claim.Target.DeploymentId && label "pole.io/resource-id" claim.ClusterId) ||
                   String.IsNullOrWhiteSpace meta.Uid || meta.DeletionTimestamp.HasValue || meta.Generation.GetValueOrDefault() <= 0L ||
                   text spec "instances" <> string cluster.Spec.Instances ||
                   text synchronous "method" <> "any" || text synchronous "number" <> "1" ||
                   text synchronous "dataDurability" <> "required" || text synchronous "failoverQuorum" <> "true" ||
                   text affinity "enablePodAntiAffinity" <> "true" || text affinity "podAntiAffinityType" <> "required" ||
                   text affinity "topologyKey" <> "kubernetes.io/hostname" ||
                   text status "readyInstances" <> string cluster.Spec.Instances || String.IsNullOrWhiteSpace(text status "currentPrimary") then
                    return None
                else
                    let selector = $"pole.io/tenant-id={claim.TenantId},pole.io/runtime-cell-id={claim.Target.RuntimeCellId},pole.io/resource-deployment-id={claim.Target.DeploymentId}"
                    let! pods = client.CoreV1.ListNamespacedPodAsync(claim.Target.KubernetesNamespace, labelSelector = selector, cancellationToken = ct)
                    let ready = pods.Items |> Seq.filter(fun pod ->
                        KubernetesOwnedWatchItem.ControlledBy(pod.Metadata, meta.Uid, "Cluster", "postgresql.cnpg.io/v1") &&
                        not pod.Metadata.DeletionTimestamp.HasValue && not (String.IsNullOrWhiteSpace pod.Metadata.Uid) && not (isNull pod.Spec) && not (String.IsNullOrWhiteSpace pod.Spec.NodeName) &&
                        not (isNull pod.Status) && not (isNull pod.Status.Conditions) && (pod.Status.Conditions |> Seq.exists(fun condition -> condition.Type = "Ready" && condition.Status = "True"))) |> Seq.toArray
                    let hosts = ready |> Array.map(fun pod -> pod.Spec.NodeName) |> Array.distinct
                    if ready.Length <> cluster.Spec.Instances || hosts.Length <> ready.Length then return None
                    else
                        let! rawQuorum = client.CustomObjects.GetNamespacedCustomObjectAsync(
                                            "postgresql.cnpg.io", "v1", claim.Target.KubernetesNamespace, "failoverquorums",
                                            PostgresManagedDatabaseManifests.clusterName claim, cancellationToken = ct)
                        let quorum = (json rawQuorum)["status"]
                        let standbyNames = field quorum "standbyNames"
                        return
                            if text quorum "method" = "ANY" && text quorum "standbyNumber" = "1" &&
                               text quorum "primary" = text status "currentPrimary" &&
                               not (isNull standbyNames) && standbyNames.AsArray().Count >= 1 then
                                let members = standbyNames.AsArray() |> Seq.map _.ToString() |> Seq.sort |> String.concat ","
                                let instances = ready |> Array.map(fun pod -> $"{pod.Metadata.Uid}@{pod.Spec.NodeName}") |> Array.sort |> String.concat ","
                                let primary = text status "currentPrimary"
                                let epoch = $"{primary}|{members}|{instances}"
                                Some(meta.Uid, meta.Generation.GetValueOrDefault(), epoch)
                            else None
            with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return None
        }
