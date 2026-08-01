namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceDeployments.Application.Reconciliation
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceDeployments.Domain

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
    deploymentManager : IPostgresClusterDeploymentManager) =

    let validateContext (context : ResourceDeploymentReconciliationContext) =
        if context.Deployment.ResourceType <> PostgresResourceDeployment.ResourceType.Value then
            Some(
                ResourceDeploymentAdapterResult.Failed.Create(
                    "ResourceTypeMismatch",
                    "The resource deployment is not PostgreSQL.",
                    false))
        elif isNull context.Deployment.ResolvedPlacement.Storage then
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
                            let storage = deployment.ResolvedPlacement.Storage
                            let metadata =
                                { DeploymentId = deployment.Id
                                  RuntimeCellId = deployment.RuntimeCellId
                                  DeploymentTargetId = deployment.ResolvedPlacement.DeploymentTargetId
                                  Generation = deployment.DesiredGeneration }

                            do!
                                deploymentManager.ApplyAsync(
                                    context.Kubernetes,
                                    cluster,
                                    credentials,
                                    deployment.ResolvedPlacement.KubernetesNamespace,
                                    storage.StorageClassName,
                                    metadata,
                                    cancellationToken)

                            let host =
                                $"{PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id}.{deployment.ResolvedPlacement.KubernetesNamespace}.svc.cluster.local"

                            return
                                ResourceDeploymentAdapterResult.Succeeded.Create(
                                    [ PostgreSqlResourceDeploymentEndpoint.Create(host, 5432) ])
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
                        do!
                            deploymentManager.DeleteAsync(
                                context.Kubernetes,
                                cluster.Id,
                                deployment.ResolvedPlacement.KubernetesNamespace,
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
