namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

type IPostgresResourceDeploymentReader =
    abstract FindAsync :
        tenantId : string *
        clusterId : string *
        cancellationToken : CancellationToken -> Task<PostgresCluster option>

type MongoPostgresResourceDeploymentReader(dbContext : IPostgresDbContext) =
    interface IPostgresResourceDeploymentReader with
        member _.FindAsync(tenantId, clusterId, cancellationToken) =
            task {
                let! cluster =
                    dbContext.Postgres
                        .Find(
                            Builders<PostgresCluster>.Filter.And(
                                Builders<PostgresCluster>.Filter.ForTenant(tenantId),
                                Builders<PostgresCluster>.Filter.Eq((fun item -> item.Id), clusterId)))
                        .FirstOrDefaultAsync(cancellationToken)

                return
                    if isNull (box cluster) then
                        None
                    else
                        Some cluster
            }

type PostgresResourceDeploymentAdapter(
    reader : IPostgresResourceDeploymentReader,
    credentialService : IPostgresCredentialService,
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
                        reader.FindAsync(
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
                            let! credentialResult =
                                credentialService.ResolveAsync(
                                    deployment.TenantId,
                                    cluster.Id,
                                    cancellationToken)

                            match credentialResult.TryGetValue() with
                            | struct (false, _, _) ->
                                return
                                    ResourceDeploymentAdapterResult.Failed.Create(
                                        "PostgresCredentialUnavailable",
                                        "The encrypted PostgreSQL credentials are unavailable.",
                                        true)
                                    :> ResourceDeploymentAdapterResult
                            | struct (true, credentials, _) ->
                                let storage = deployment.Placement.Storage
                                let metadata =
                                    { DeploymentId = deployment.Id
                                      RuntimeCellId = deployment.RuntimeCellId
                                      DeploymentTargetId = deployment.Placement.DeploymentTargetId
                                      Generation = deployment.DesiredGeneration }

                                let! kubernetes =
                                    kubernetesClientProvider.GetAsync(
                                        deployment.Placement.DeploymentTargetId,
                                        cancellationToken)

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
                                        [ ResourceDeploymentEndpointDto.PostgreSql(host, 5432) ])
                                    :> ResourceDeploymentAdapterResult
            }

        member _.DeleteAsync(context, cancellationToken) =
            task {
                match validateContext context with
                | Some failure -> return failure :> ResourceDeploymentAdapterResult
                | None ->
                    let deployment = context.Deployment
                    let! cluster =
                        reader.FindAsync(
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
