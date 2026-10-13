namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open k8s
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

type PostgresResourceDeploymentObserver(loadCluster : LoadPostgresClusterById, clients : IKubernetesClientProvider, settings : PostgresSettings) =
    new(loadCluster, clients) = PostgresResourceDeploymentObserver(loadCluster, clients, PostgresSettings())
    member private _.Identity(deployment : ResourceDeploymentReconciliationSnapshot) =
        WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId, deployment.IsolationScopeId,
            deployment.RuntimeCellId, deployment.Placement.DeploymentTargetId, "Postgres", deployment.ResourceId, deployment.Id)

    member private _.ControllerMetadataAsync(cluster : PostgresCluster, deployment : ResourceDeploymentReconciliationSnapshot, client : IKubernetes, ct : CancellationToken) =
        task {
            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let ns = deployment.Placement.KubernetesNamespace
            match cluster.Spec.DeploymentMode with
            | PostgresDeploymentMode.StatefulSet ->
                let! controller = client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken = ct)
                return controller.Metadata
            | PostgresDeploymentMode.Operator ->
                let! native = client.CustomObjects.GetNamespacedCustomObjectAsync("postgresql.cnpg.io", "v1", ns, "clusters", name, cancellationToken = ct)
                try return PostgresClusterObservation.metadata native
                with :? JsonException -> return null
            | mode -> return invalidOp $"Unsupported PostgreSQL mode '{mode}'."
        }

    member private this.WorkloadAsync(cluster : PostgresCluster, deployment : ResourceDeploymentReconciliationSnapshot, client : IKubernetes, ct : CancellationToken) =
        task {
            let identity = this.Identity(deployment)
            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let ns = deployment.Placement.KubernetesNamespace
            match cluster.Spec.DeploymentMode with
            | PostgresDeploymentMode.StatefulSet ->
                return! KubernetesWorkloadDiagnostics.StatefulSetAsync(client, name, ns, identity, deployment.DesiredGeneration, "pole.io/deployment-generation", true, ct)
            | PostgresDeploymentMode.Operator ->
                let! meta = this.ControllerMetadataAsync(cluster, deployment, client, ct)
                return if isNull meta then null else KubernetesWorkloadDiagnostics.Controller(client, ns, identity, meta, "Cluster", deployment.DesiredGeneration, "pole.io/deployment-generation", true)
            | mode -> return invalidOp $"Unsupported PostgreSQL mode '{mode}'."
        }

    member private this.ObserveNativeAsync(cluster : PostgresCluster, deployment : ResourceDeploymentReconciliationSnapshot, client : IKubernetes, ct : CancellationToken) =
        task {
            let identity = this.Identity(deployment)
            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let ns = deployment.Placement.KubernetesNamespace
            match cluster.Spec.DeploymentMode with
            | PostgresDeploymentMode.StatefulSet ->
                return! KubernetesWorkloadObservation.StatefulSetAsync(client, name, ns, identity, deployment.DesiredGeneration, "pole.io/deployment-generation", true, ct)
            | PostgresDeploymentMode.Operator ->
                let! native = client.CustomObjects.GetNamespacedCustomObjectAsync("postgresql.cnpg.io", "v1", ns, "clusters", name, cancellationToken = ct)
                match PostgresClusterObservation.tryRead native with
                | None -> return UnavailableWorkloadObservation("InvalidNativeObservation") :> WorkloadObservation
                | Some observed ->
                    let! conforming = if observed.RequiresPodConformance then PostgresOperatorReadiness.matchesAsync observed cluster deployment settings client ct else Threading.Tasks.Task.FromResult(false)
                    return! KubernetesWorkloadObservation.PodsAsync(client, ns, identity, observed.Metadata,
                        HashSet<string>([ observed.Metadata.Uid ], StringComparer.Ordinal), deployment.DesiredGeneration, "pole.io/deployment-generation", true,
                        (observed.ReadyCondition || conforming) && KubernetesWorkloadObservation.Generation(observed.Metadata, "pole.io/deployment-generation", true) = deployment.DesiredGeneration,
                        observed.Desired, observed.Current, observed.Ready, observed.Ready, observed.Ready, ct)
            | mode -> return invalidOp $"Unsupported PostgreSQL mode '{mode}'."
        }

    member private this.ResolveDiagnosticsAsync(deployment : ResourceDeploymentReconciliationSnapshot, ct : CancellationToken) =
        task {
            if deployment.ResourceType <> PostgresResourceDeployment.ResourceType then return null
            else
                let! cluster = loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, ct)
                match cluster with
                | None -> return null
                | Some cluster ->
                    let! client = clients.GetAsync(deployment.Placement.DeploymentTargetId, ct)
                    return! this.WorkloadAsync(cluster, deployment, client, ct)
        }

    interface IResourceDeploymentDiagnosticsProvider with
        member this.DiscoverAsync(deployment, ct) = task {
            let! workload = this.ResolveDiagnosticsAsync(deployment, ct)
            return! KubernetesWorkloadDiagnostics.DiscoverAsync(workload, ct) }
        member this.OpenLogsAsync(deployment, selection, ct) =
            KubernetesWorkloadDiagnostics.OpenAsync(Func<CancellationToken, Threading.Tasks.Task<OwnedKubernetesWorkload>>(fun token -> this.ResolveDiagnosticsAsync(deployment, token)), selection, ct)

    interface IResourceDeploymentObserver with
        member this.ObserveAsync(deployment, ct) =
            task {
                if deployment.ResourceType <> PostgresResourceDeployment.ResourceType then return UnavailableWorkloadObservation("ResourceTypeMismatch") :> WorkloadObservation
                else
                    let! cluster = loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, ct)
                    match cluster with
                    | None -> return UnavailableWorkloadObservation("ResourceNotFound") :> WorkloadObservation
                    | Some cluster ->
                        let! client = clients.GetAsync(deployment.Placement.DeploymentTargetId, ct)
                        return! this.ObserveNativeAsync(cluster, deployment, client, ct)
            }

    interface IResourceDeploymentBoundObserver with
        member this.ObserveBoundAsync(context, bindingId, ct) =
            task {
                let deployment = context.Deployment
                let changed () = ResourceDeploymentBoundObservation(null, UnavailableWorkloadObservation("BindingChanged"), Diagnostics = UnavailableWorkloadDiagnostics("BindingChanged"))
                if deployment.ResourceType <> PostgresResourceDeployment.ResourceType then return ResourceDeploymentBoundObservation(null, UnavailableWorkloadObservation("ResourceTypeMismatch"))
                else
                    let! cluster = loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, ct)
                    match cluster with
                    | None -> return ResourceDeploymentBoundObservation(null, UnavailableWorkloadObservation("ResourceNotFound"))
                    | Some cluster ->
                        let identity = this.Identity(deployment)
                        let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
                        let ns = deployment.Placement.KubernetesNamespace
                        let! client = clients.GetAsync(deployment.Placement.DeploymentTargetId, ct)
                        let mutable generation = Nullable<int64>()
                        let matches () = task {
                            let! meta = this.ControllerMetadataAsync(cluster, deployment, client, ct)
                            let valid =
                                not (isNull meta) && meta.Name = name && meta.NamespaceProperty = ns && meta.Uid = bindingId &&
                                KubernetesWorkloadObservation.Owns(meta, identity) && (not generation.HasValue || meta.Generation = generation)
                            if valid && not generation.HasValue then generation <- meta.Generation
                            return valid }
                        let! before = matches ()
                        if not before then return changed ()
                        else
                            let! observed = this.ObserveNativeAsync(cluster, deployment, client, ct)
                            let! workload = this.WorkloadAsync(cluster, deployment, client, ct)
                            if isNull workload || workload.Controller.Uid <> bindingId then return changed ()
                            else
                                let! diagnostics = KubernetesWorkloadDiagnostics.DiscoverAsync(workload, ct)
                                let! after = matches ()
                                if not after then return changed ()
                                else return ResourceDeploymentBoundObservation(bindingId, observed, Diagnostics = diagnostics)
            }
