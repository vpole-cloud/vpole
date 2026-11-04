namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Workloads

[<RequireQualifiedAccess>]
module PostgresIngressPolicyDefinitions =
    [<Literal>]
    let ReplicationPurpose = "postgres-replication"

    [<Literal>]
    let OperatorPurpose = "postgres-operator"

    [<Literal>]
    let OperatorSourceName = "postgres/operator"

    let private workloadLabels (request : ResourceIngressPolicyRequest) =
        ImmutableDictionary<string, string>.Empty
            .Add("pole.io/tenant-id", request.TenantId)
            .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
            .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId)

    let Replication =
        ResourceIngressPolicyDefinition(
            PostgresResourceDeployment.ResourceType,
            ReplicationPurpose,
            false,
            Func<ImmutableList<NetworkPort>, bool>(fun ports ->
                ports.Count = 1 && ports[0].Value = 5432),
            Func<ResourceIngressPolicyRequest, ImmutableDictionary<string, string>>(workloadLabels),
            Func<ResourceIngressPolicyRequest, ResourceIngressPolicyTargetSources, ResourceIngressPolicyNetworkSource>(
                fun request _ ->
                    ResourceIngressPolicyPodSource(
                        request.KubernetesNamespace,
                        ImmutableDictionary<string, string>.Empty
                            .Add("pole.io/tenant-id", request.TenantId)
                            .Add("pole.io/resource-id", request.ResourceId)
                            .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId))))

    let Operator =
        ResourceIngressPolicyDefinition(
            PostgresResourceDeployment.ResourceType,
            OperatorPurpose,
            false,
            Func<ImmutableList<NetworkPort>, bool>(fun ports ->
                ports.Count = 2 &&
                (ports |> Seq.map (fun port -> port.Value) |> Seq.sort |> Seq.toArray) = [| 5432; 8000 |]),
            Func<ResourceIngressPolicyRequest, ImmutableDictionary<string, string>>(workloadLabels),
            Func<ResourceIngressPolicyRequest, ResourceIngressPolicyTargetSources, ResourceIngressPolicyNetworkSource>(
                fun _ sources -> sources.Get(OperatorSourceName)))

type PostgresResourceDeploymentAdapter(
    loadCluster : LoadPostgresClusterById,
    readCredentialKey : ReadManagedSecretKey,
    ensureProjection : EnsureManagedSecretProjection,
    deploymentManager : IPostgresClusterDeploymentManager,
    kubernetesClientProvider : IKubernetesClientProvider,
    reconcileIngressPolicy : ReconcileResourceIngressPolicy) =

    let ingressRequest
        (deployment : ResourceDeploymentReconciliationSnapshot)
        purpose
        ports =
        ResourceIngressPolicyRequest(
            deployment.TenantId,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            deployment.Placement.KubernetesNamespace,
            purpose,
            deployment.DesiredGeneration,
            deployment.Placement.TargetWorkloadConfigurationRevision,
            ImmutableList.CreateRange<NetworkPort>(ports |> Seq.map NetworkPort.Create))

    let ingressApplied (outcome : ReconcileResourceIngressPolicyOutcome) =
        outcome.Value :? ResourceIngressPolicyApplied

    let ensureApplyIngress deployment mode cancellationToken =
        task {
            let! replication =
                reconcileIngressPolicy.Invoke(
                    ingressRequest deployment PostgresIngressPolicyDefinitions.ReplicationPurpose [ 5432 ],
                    cancellationToken)
            if not (ingressApplied replication) then
                return Some("PostgresReplicationIngressUnavailable", "The PostgreSQL replication ingress policy is not ready.")
            elif mode = PostgresDeploymentMode.Operator then
                let! operator =
                    reconcileIngressPolicy.Invoke(
                        ingressRequest deployment PostgresIngressPolicyDefinitions.OperatorPurpose [ 5432; 8000 ],
                        cancellationToken)
                return
                    if ingressApplied operator then None
                    else Some("PostgresOperatorIngressUnavailable", "The PostgreSQL operator ingress policy is not ready.")
            else
                return None
        }

    let ensureDeleteIngress deployment cancellationToken =
        task {
            let! replication =
                reconcileIngressPolicy.Invoke(
                    ingressRequest deployment PostgresIngressPolicyDefinitions.ReplicationPurpose [],
                    cancellationToken)
            let! operator =
                reconcileIngressPolicy.Invoke(
                    ingressRequest deployment PostgresIngressPolicyDefinitions.OperatorPurpose [],
                    cancellationToken)
            return
                if not (ingressApplied replication) then
                    Some("PostgresReplicationIngressCleanupUnavailable", "The PostgreSQL replication ingress policy could not be closed.")
                elif not (ingressApplied operator) then
                    Some("PostgresOperatorIngressCleanupUnavailable", "The PostgreSQL operator ingress policy could not be closed.")
                else
                    None
        }

    let validateContext (context : ResourceDeploymentReconciliationContext) =
        if context.Deployment.ResourceType <> PostgresResourceDeployment.ResourceType then
            Some(
                ResourceDeploymentAdapterResult.Failed.Create(
                    "ResourceTypeMismatch",
                    "The resource deployment is not PostgreSQL.",
                    false))
        elif isNull context.Deployment.Placement.Storage then
            Some(
                ResourceDeploymentAdapterResult.Failed.Create(
                    "StorageConfigurationMissing",
                    "The PostgreSQL placement does not contain resolved storage configuration.",
                    false))
        else
            None

    interface IResourceDeploymentAdapter with
        member _.ApplyAsync(context, cancellationToken) =
            task {
                match validateContext context with
                | Some failure -> return failure :> ResourceDeploymentAdapterResult
                | None ->
                    let deployment = context.Deployment
                    let! cluster =
                        loadCluster.Invoke(
                            deployment.TenantId,
                            deployment.ResourceId,
                            cancellationToken)

                    match cluster with
                    | None ->
                        return
                            ResourceDeploymentAdapterResult.Failed.Create(
                                "PostgresClusterNotFound",
                                "The logical PostgreSQL cluster no longer exists.",
                                false)
                            :> ResourceDeploymentAdapterResult
                    | Some cluster ->
                        if cluster.Spec.DeploymentMode = PostgresDeploymentMode.Operator &&
                           not (String.IsNullOrWhiteSpace deployment.Placement.Compute.RuntimeClassName) then
                            return
                                ResourceDeploymentAdapterResult.Failed.Create(
                                    "ComputeRuntimeClassUnsupported",
                                    "CloudNativePG compute profiles cannot specify a runtime class.",
                                    false)
                                :> ResourceDeploymentAdapterResult
                        else
                            let! ingressFailure =
                                ensureApplyIngress deployment cluster.Spec.DeploymentMode cancellationToken
                            match ingressFailure with
                            | Some(code, message) ->
                                return ResourceDeploymentAdapterResult.Failed.Create(
                                    code,
                                    message,
                                    true) :> ResourceDeploymentAdapterResult
                            | None ->
                                let! projectionResult =
                                    ensureProjection.Invoke(
                                        EnsureManagedSecretProjectionRequest(
                                            deployment.TenantId,
                                            PostgresManagedSecret.owner cluster.Id,
                                            deployment.Id),
                                        cancellationToken)
                                match projectionResult.Value with
                                | :? ManagedSecretUnavailable ->
                                    return ResourceDeploymentAdapterResult.Failed.Create(
                                        "PostgresSecretProjectionUnavailable",
                                        "The managed credential projection is not ready.",
                                        true) :> ResourceDeploymentAdapterResult
                                | :? SecretProjectionReference as projection ->
                                    let storage = deployment.Placement.Storage
                                    let metadata =
                                        { DeploymentId = deployment.Id
                                          IsolationScopeId = deployment.IsolationScopeId
                                          RuntimeCellId = deployment.RuntimeCellId
                                          DeploymentTargetId = deployment.Placement.DeploymentTargetId
                                          Generation = deployment.DesiredGeneration }
                                    let! kubernetes =
                                        kubernetesClientProvider.GetAsync(
                                            deployment.Placement.DeploymentTargetId,
                                            cancellationToken)
                                    let! projectedCredentials =
                                        task {
                                            match cluster.Spec.DeploymentMode with
                                            | PostgresDeploymentMode.StatefulSet ->
                                                return Ok(StatefulSetCredentials projection)
                                            | PostgresDeploymentMode.Operator ->
                                                let! username = readCredentialKey.Invoke(
                                                    ReadManagedSecretKeyRequest(
                                                        deployment.TenantId,
                                                        PostgresManagedSecret.owner cluster.Id,
                                                        PostgresManagedSecret.usernameKey),
                                                    cancellationToken)
                                                return
                                                    match username.Value with
                                                    | :? ManagedSecretKey as key ->
                                                        Ok(OperatorCredentials(key.Value, projection))
                                                    | :? ManagedSecretFailure ->
                                                        Error()
                                                    | _ -> invalidOp "Unexpected managed Secret key outcome."
                                            | mode ->
                                                return invalidOp $"Unsupported PostgreSQL deployment mode '{mode}'."
                                        }
                                    match projectedCredentials with
                                    | Error _ ->
                                        return ResourceDeploymentAdapterResult.Failed.Create(
                                            "PostgresCredentialUnavailable",
                                            "The managed PostgreSQL username is unavailable.",
                                            true) :> ResourceDeploymentAdapterResult
                                    | Ok credentials ->
                                        do!
                                            deploymentManager.ApplyAsync(
                                                kubernetes,
                                                cluster,
                                                credentials,
                                                deployment.Placement.KubernetesNamespace,
                                                storage.StorageClassName,
                                                PostgresResourceDeploymentMappings.kubernetesCompute
                                                    deployment.Placement.Compute,
                                                metadata,
                                                cancellationToken)
                                        let host =
                                            $"{PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id}.{deployment.Placement.KubernetesNamespace}.svc.cluster.local"
                                        return
                                            ResourceDeploymentAdapterResult.Succeeded.Create(
                                                [ ResourceDeploymentEndpointDto.Create(
                                                      "PostgreSql",
                                                      host,
                                                      5432,
                                                      "postgresql") ])
                                            :> ResourceDeploymentAdapterResult

                                | _ -> return invalidOp "Unexpected managed Secret projection outcome."

            }

        member _.DeleteAsync(context, cancellationToken) =
            task {
                match validateContext context with
                | Some failure -> return failure :> ResourceDeploymentAdapterResult
                | None ->
                    let deployment = context.Deployment
                    let! ingressFailure = ensureDeleteIngress deployment cancellationToken
                    match ingressFailure with
                    | Some(code, message) ->
                        return ResourceDeploymentAdapterResult.Failed.Create(
                            code,
                            message,
                            true) :> ResourceDeploymentAdapterResult
                    | None ->
                        let! cluster =
                            loadCluster.Invoke(
                                deployment.TenantId,
                                deployment.ResourceId,
                                cancellationToken)

                        match cluster with
                        | None ->
                            return
                                ResourceDeploymentAdapterResult.Failed.Create(
                                    "PostgresClusterNotFound",
                                    "The logical PostgreSQL cluster no longer exists.",
                                    false)
                                :> ResourceDeploymentAdapterResult
                        | Some cluster ->
                            let! kubernetes =
                                kubernetesClientProvider.GetAsync(
                                    deployment.Placement.DeploymentTargetId,
                                    cancellationToken)

                            do!
                                deploymentManager.DeleteAsync(
                                    kubernetes,
                                    cluster.Id,
                                    deployment.Placement.KubernetesNamespace,
                                    cluster.Spec.DeploymentMode,
                                    cancellationToken)

                            return ResourceDeploymentAdapterResult.Succeeded.Create()
                                   :> ResourceDeploymentAdapterResult
            }

[<AbstractClass; Sealed>]
type PostgresResourceDeploymentAdapterRegistration =
    static member Descriptor =
        ResourceDeploymentAdapterDescriptor.Create<PostgresResourceDeploymentAdapter>(
            PostgresResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget)
