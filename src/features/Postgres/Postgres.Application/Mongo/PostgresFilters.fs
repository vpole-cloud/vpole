// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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

    let forList tenantId : FilterDefinition<PostgresCluster> = visibleToTenant tenantId
