namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Threading
open System.Threading.Tasks
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Workloads

type PostgresResourceDeploymentAdapter(
    loadCluster : LoadPostgresClusterById,
    readCredentialKey : ReadManagedSecretKey,
    ensureProjection : EnsureManagedSecretProjection,
    deploymentManager : IPostgresClusterDeploymentManager,
    kubernetesClientProvider : IKubernetesClientProvider) =

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
                            let! projectionResult =
                                ensureProjection.Invoke(
                                    deployment.TenantId,
                                    PostgresManagedSecret.owner cluster.Id,
                                    deployment.Id,
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
                                                deployment.TenantId,
                                                PostgresManagedSecret.owner cluster.Id,
                                                PostgresManagedSecret.usernameKey,
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

                        return
                            ResourceDeploymentAdapterResult.Succeeded.Create()
                            :> ResourceDeploymentAdapterResult
            }

[<AbstractClass; Sealed>]
type PostgresResourceDeploymentAdapterRegistration =
    static member Descriptor =
        ResourceDeploymentAdapterDescriptor.Create<PostgresResourceDeploymentAdapter>(
            PostgresResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget)
