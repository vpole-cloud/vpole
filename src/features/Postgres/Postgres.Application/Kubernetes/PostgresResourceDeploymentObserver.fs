namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Collections.Generic
open System.Text.Json
open k8s
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

type PostgresResourceDeploymentObserver(loadCluster : LoadPostgresClusterById, clients : IKubernetesClientProvider) =
    member private _.ResolveDiagnosticsAsync(deployment : ResourceDeploymentReconciliationSnapshot, cancellationToken : Threading.CancellationToken) =
        task {
            if deployment.ResourceType <> PostgresResourceDeployment.ResourceType then return null
            else
                let! cluster = loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, cancellationToken)
                match cluster with
                | None -> return null
                | Some cluster ->
                    let identity = WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId, deployment.IsolationScopeId,
                                       deployment.RuntimeCellId, deployment.Placement.DeploymentTargetId, "Postgres", deployment.ResourceId, deployment.Id)
                    let! client = clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken)
                    let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
                    let namespaceName = deployment.Placement.KubernetesNamespace
                    match cluster.Spec.DeploymentMode with
                    | PostgresDeploymentMode.StatefulSet ->
                        return! KubernetesWorkloadDiagnostics.StatefulSetAsync(client, name, namespaceName, identity,
                                    deployment.DesiredGeneration, "pole.io/deployment-generation", true, cancellationToken)
                    | PostgresDeploymentMode.Operator ->
                        let! native = client.CustomObjects.GetNamespacedCustomObjectAsync("postgresql.cnpg.io", "v1", namespaceName, "clusters", name, cancellationToken = cancellationToken)
                        use document = JsonDocument.Parse(JsonSerializer.Serialize(native))
                        let metadata = KubernetesJson.Deserialize<V1ObjectMeta>(document.RootElement.GetProperty("metadata").GetRawText())
                        return KubernetesWorkloadDiagnostics.Controller(client, namespaceName, identity, metadata, "Cluster",
                                   deployment.DesiredGeneration, "pole.io/deployment-generation", true)
                    | mode -> return invalidOp $"Unsupported PostgreSQL mode '{mode}'."
        }
    interface IResourceDeploymentDiagnosticsProvider with
        member this.DiscoverAsync(deployment, cancellationToken) =
            task {
                let! workload = this.ResolveDiagnosticsAsync(deployment, cancellationToken)
                return! KubernetesWorkloadDiagnostics.DiscoverAsync(workload, cancellationToken)
            }
        member this.OpenLogsAsync(deployment, selection, cancellationToken) =
            KubernetesWorkloadDiagnostics.OpenAsync(Func<Threading.CancellationToken, Threading.Tasks.Task<OwnedKubernetesWorkload>>(fun token -> this.ResolveDiagnosticsAsync(deployment, token)), selection, cancellationToken)
    interface IResourceDeploymentObserver with
        member _.ObserveAsync(deployment, cancellationToken) =
            task {
                if deployment.ResourceType <> PostgresResourceDeployment.ResourceType then
                    return UnavailableWorkloadObservation("ResourceTypeMismatch") :> WorkloadObservation
                else
                    let! cluster = loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, cancellationToken)
                    match cluster with
                    | None -> return UnavailableWorkloadObservation("ResourceNotFound") :> WorkloadObservation
                    | Some cluster ->
                        // PostgreSQL's existing native manifests use this provider-owned storage label.
                        let identity = WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId,
                                           deployment.IsolationScopeId, deployment.RuntimeCellId,
                                           deployment.Placement.DeploymentTargetId, "Postgres", deployment.ResourceId, deployment.Id)
                        let! client = clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken)
                        let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
                        let namespaceName = deployment.Placement.KubernetesNamespace
                        match cluster.Spec.DeploymentMode with
                        | PostgresDeploymentMode.StatefulSet ->
                            return! KubernetesWorkloadObservation.StatefulSetAsync(client, name, namespaceName,
                                        identity, deployment.DesiredGeneration, "pole.io/deployment-generation", true, cancellationToken)
                        | PostgresDeploymentMode.Operator ->
                            let! native = client.CustomObjects.GetNamespacedCustomObjectAsync(
                                              "postgresql.cnpg.io", "v1", namespaceName, "clusters", name,
                                              cancellationToken = cancellationToken)
                            use document = JsonDocument.Parse(JsonSerializer.Serialize(native))
                            let root = document.RootElement
                            let metadata = KubernetesJson.Deserialize<V1ObjectMeta>(root.GetProperty("metadata").GetRawText())
                            let integer (parent : JsonElement) (key : string) =
                                let mutable value = Unchecked.defaultof<JsonElement>
                                if parent.TryGetProperty(key, &value) && value.ValueKind = JsonValueKind.Number then value.GetInt32() else 0
                            let mutable status = Unchecked.defaultof<JsonElement>
                            let hasStatus = root.TryGetProperty("status", &status)
                            let count = if hasStatus then integer status "readyInstances" else 0
                            let mutable conditions = Unchecked.defaultof<JsonElement>
                            let readyCondition = hasStatus && status.TryGetProperty("conditions", &conditions) &&
                                                 (conditions.EnumerateArray() |> Seq.exists (fun condition ->
                                                     let mutable kind = Unchecked.defaultof<JsonElement>
                                                     let mutable state = Unchecked.defaultof<JsonElement>
                                                     let mutable generation = Unchecked.defaultof<JsonElement>
                                                     condition.TryGetProperty("type", &kind) && kind.GetString() = "Ready" &&
                                                     condition.TryGetProperty("status", &state) && state.GetString() = "True" &&
                                                     condition.TryGetProperty("observedGeneration", &generation) &&
                                                     generation.GetInt64() >= metadata.Generation.GetValueOrDefault()))
                            let desired = integer (root.GetProperty("spec")) "instances"
                            return! KubernetesWorkloadObservation.PodsAsync(client, namespaceName, identity, metadata,
                                        HashSet<string>([ metadata.Uid ], StringComparer.Ordinal), deployment.DesiredGeneration,
                                        "pole.io/deployment-generation", true,
                                        readyCondition && KubernetesWorkloadObservation.Generation(metadata, "pole.io/deployment-generation", true) = deployment.DesiredGeneration,
                                        desired, (if hasStatus then integer status "instances" else 0),
                                        count, count, count, cancellationToken)
                        | mode -> return invalidOp $"Unsupported PostgreSQL mode '{mode}'."
            }
