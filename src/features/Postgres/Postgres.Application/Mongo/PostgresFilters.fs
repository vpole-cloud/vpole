namespace Pole.Features.Postgres.Application.Mongo

open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain

[<RequireQualifiedAccess>]
module PostgresFilters =
    let private clusters = Builders<PostgresCluster>.Filter

    let visibleToTenant (tenantId: string) : FilterDefinition<PostgresCluster> = clusters.ForTenant tenantId

    let byId tenantId clusterId : FilterDefinition<PostgresCluster> =
        clusters.And(visibleToTenant tenantId, clusters.Eq((fun (cluster: PostgresCluster) -> cluster.Id), clusterId))

    let byIds
        tenantId
        (clusterIds: System.Collections.Generic.IReadOnlyCollection<string>)
        : FilterDefinition<PostgresCluster> =
        clusters.And(visibleToTenant tenantId, clusters.In((fun (cluster: PostgresCluster) -> cluster.Id), clusterIds))

    let byName tenantId name : FilterDefinition<PostgresCluster> =
        clusters.And(visibleToTenant tenantId, clusters.Eq((fun (cluster: PostgresCluster) -> cluster.Name), name))

    let atRevision (cluster: PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            byId cluster.TenantId cluster.Id,
            clusters.Eq((fun (candidate: PostgresCluster) -> candidate.Revision), cluster.Revision),
            clusters.Eq((fun (candidate: PostgresCluster) -> candidate.PlanningFence), cluster.PlanningFence)
        )

    let activeAtRevision (cluster: PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            atRevision cluster,
            clusters.OfType<PostgresClusterLifecycle, ActivePostgresClusterLifecycle>(fun candidate ->
                candidate.Lifecycle)
        )

    let deletingAtRevision (cluster: PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            atRevision cluster,
            clusters.OfType<PostgresClusterLifecycle, DeletingPostgresClusterLifecycle>(fun candidate ->
                candidate.Lifecycle)
        )

    let deletingById tenantId clusterId : FilterDefinition<PostgresCluster> =
        clusters.And(
            byId tenantId clusterId,
            clusters.OfType<PostgresClusterLifecycle, DeletingPostgresClusterLifecycle>(fun candidate ->
                candidate.Lifecycle)
        )

    let synchronousHa tenantId =
        clusters.And(
            visibleToTenant tenantId,
            clusters.Eq((fun (item: PostgresCluster) -> item.Spec.ReplicationMode), PostgresReplicationMode.SynchronousHa),
            clusters.Gte((fun (item: PostgresCluster) -> item.Spec.Instances), 3),
            clusters.OfType<PostgresClusterLifecycle, ActivePostgresClusterLifecycle>(fun item -> item.Lifecycle),
            clusters.Not(clusters.OfType<PostgresProvisioningState, PreparedPostgresProvisioning>(fun item -> item.Provisioning)))

    let forList tenantId : FilterDefinition<PostgresCluster> = visibleToTenant tenantId


[<RequireQualifiedAccess>]
module PostgresDatabaseFilters =
    let private claims = Builders<PostgresManagedDatabase>.Filter

    let owner (tenantId: string) resourceType resourceId =
        claims.And(
            claims.ForTenant tenantId,
            claims.Eq((fun (item: PostgresManagedDatabase) -> item.Owner.ResourceType), resourceType),
            claims.Eq((fun (item: PostgresManagedDatabase) -> item.Owner.ResourceId), resourceId))

    let identity (tenantId: string) id =
        claims.And(claims.ForTenant tenantId, claims.Eq((fun (item: PostgresManagedDatabase) -> item.Id), id))

    let atRevision (claim: PostgresManagedDatabase) =
        claims.And(identity claim.TenantId claim.Id,
            claims.Eq((fun (item: PostgresManagedDatabase) -> item.Revision), claim.Revision))

    let liveForCluster (tenantId: string) clusterId =
        claims.And(claims.ForTenant tenantId,
            claims.Eq((fun (item: PostgresManagedDatabase) -> item.ClusterId), clusterId),
            claims.Not(claims.OfType<PostgresManagedDatabaseState, PurgedPostgresManagedDatabase>(fun item -> item.State)))

    let recoveryCandidates after =
        match after with
        | None -> claims.Empty
        | Some id -> claims.Gt((fun (item: PostgresManagedDatabase) -> item.Id), id)
