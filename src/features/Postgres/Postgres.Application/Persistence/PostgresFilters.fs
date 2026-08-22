namespace Pole.Features.Postgres.Application.Persistence

open MongoDB.Bson
open MongoDB.Driver
open Pole.Database
open Pole.Features.Postgres.Domain

[<RequireQualifiedAccess>]
module PostgresFilters =
    let private clusters = Builders<PostgresCluster>.Filter
    let private credentials = Builders<PostgresCredential>.Filter

    let visibleToTenant (tenantId : string) : FilterDefinition<PostgresCluster> =
        clusters.ForTenant tenantId

    let byId tenantId clusterId : FilterDefinition<PostgresCluster> =
        clusters.And(
            visibleToTenant tenantId,
            clusters.Eq((fun (cluster : PostgresCluster) -> cluster.Id), clusterId))

    let atRevision (cluster : PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            byId cluster.TenantId cluster.Id,
            clusters.Eq((fun (candidate : PostgresCluster) -> candidate.Revision), cluster.Revision))

    let activeAtRevision (cluster : PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            atRevision cluster,
            clusters.OfType<PostgresClusterLifecycle, ActivePostgresClusterLifecycle>(
                fun candidate -> candidate.Lifecycle))

    let deletingAtRevision (cluster : PostgresCluster) : FilterDefinition<PostgresCluster> =
        clusters.And(
            atRevision cluster,
            clusters.OfType<PostgresClusterLifecycle, DeletingPostgresClusterLifecycle>(
                fun candidate -> candidate.Lifecycle))

    let deletingById tenantId clusterId : FilterDefinition<PostgresCluster> =
        clusters.And(
            byId tenantId clusterId,
            clusters.OfType<PostgresClusterLifecycle, DeletingPostgresClusterLifecycle>(
                fun candidate -> candidate.Lifecycle))

    let forList tenantId : FilterDefinition<PostgresCluster> =
        visibleToTenant tenantId

    let credentialByCluster tenantId clusterId : FilterDefinition<PostgresCredential> =
        credentials.And(
            credentials.Eq((fun (credential : PostgresCredential) -> credential.TenantId), tenantId),
            credentials.Eq((fun (credential : PostgresCredential) -> credential.PostgresClusterId), clusterId))

    let credentialAtVersion (credential : PostgresCredential) : FilterDefinition<PostgresCredential> =
        credentials.And(
            credentialByCluster credential.TenantId credential.PostgresClusterId,
            credentials.Eq((fun (candidate : PostgresCredential) -> candidate.Id), credential.Id),
            credentials.Eq((fun (candidate : PostgresCredential) -> candidate.Version), credential.Version))

    let credentialForDeletedCluster tenantId clusterId : FilterDefinition<PostgresCredential> =
        credentialByCluster tenantId clusterId
