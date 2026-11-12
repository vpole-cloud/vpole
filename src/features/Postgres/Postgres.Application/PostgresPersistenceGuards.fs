namespace Pole.Features.Postgres.Application

open System
open MongoDB.Bson
open MongoDB.Driver
open Pole.Features.Postgres.Domain

[<RequireQualifiedAccess>]
module PostgresPersistenceGuards =
    let private ensureObjectId name value =
        if not (ObjectId.TryParse value |> fst) then
            invalidArg name "PostgreSQL identity must be a MongoDB ObjectId."

    let identity tenantId clusterId =
        ensureObjectId "tenantId" tenantId
        ensureObjectId "clusterId" clusterId

    let cluster (cluster: PostgresCluster) =
        if isNull (box cluster) then
            nullArg "cluster"

        identity cluster.TenantId cluster.Id
        ensureObjectId "cluster.GroupId" cluster.GroupId
        ensureObjectId "cluster.Spec.StorageProfileId" cluster.Spec.StorageProfileId

        if String.IsNullOrWhiteSpace cluster.Name then
            invalidArg "cluster" "PostgreSQL cluster name is required."

        if cluster.Spec.Instances < 1 then
            invalidArg "cluster" "PostgreSQL instance count must be positive."

        if cluster.Spec.Storage < 1 then
            invalidArg "cluster" "PostgreSQL storage must be positive."

        if cluster.PlanningFence < 0L then
            invalidArg "cluster" "PostgreSQL planning fence must be nonnegative."

        if isNull (box cluster.Provisioning) then
            invalidArg "cluster" "PostgreSQL provisioning state is required."

    let listQuery tenantId pageNumber pageSize =
        ensureObjectId "tenantId" tenantId

        if pageNumber < 1 then
            invalidArg "pageNumber" "Page number must be greater than zero."

        if pageSize < 1 then
            invalidArg "pageSize" "Page size must be greater than zero."

    let sameIdentity (current: PostgresCluster) (replacement: PostgresCluster) =
        cluster current
        cluster replacement

        if
            current.TenantId <> replacement.TenantId
            || current.Id <> replacement.Id
            || current.Name <> replacement.Name
        then
            invalidOp "A PostgreSQL cluster replacement must preserve tenant, resource identity, and immutable name."

        if
            current.GroupId <> replacement.GroupId
            || current.Spec.Version <> replacement.Spec.Version
            || current.Spec.StorageProfileId <> replacement.Spec.StorageProfileId
        then
            invalidOp "A PostgreSQL cluster replacement must preserve group, version, mode, and storage identity."

        if current.PlanningFence <> replacement.PlanningFence then
            invalidOp "Only a planning fence operation may change the PostgreSQL planning fence."

        match current.Provisioning, replacement.Provisioning with
        | :? DirectPostgresProvisioning, :? DirectPostgresProvisioning -> ()
        | (:? PreparedPostgresProvisioning as before), (:? PreparedPostgresProvisioning as after) when
            before.Bootstrap = after.Bootstrap
            ->
            ()
        | (:? PreparedPostgresProvisioning as before), (:? ConfiguredPostgresProvisioning as after) when
            before.Bootstrap = after.Bootstrap
            ->
            ()
        | (:? ConfiguredPostgresProvisioning as before), (:? ConfiguredPostgresProvisioning as after) when
            before.Bootstrap = after.Bootstrap
            ->
            ()
        | _ -> invalidOp "A PostgreSQL cluster replacement must preserve its immutable bootstrap identity."

    let activeUpdate (current: PostgresCluster) (replacement: PostgresCluster) =
        sameIdentity current replacement

        match current.Lifecycle, replacement.Lifecycle with
        | :? ActivePostgresClusterLifecycle, :? ActivePostgresClusterLifecycle -> ()
        | _ -> invalidOp "PostgreSQL updates must replace an active cluster with an active cluster."

    let deletionTransition (current: PostgresCluster) (replacement: PostgresCluster) =
        sameIdentity current replacement

        match current.Lifecycle, replacement.Lifecycle with
        | :? ActivePostgresClusterLifecycle, :? DeletingPostgresClusterLifecycle -> ()
        | _ -> invalidOp "PostgreSQL deletion must transition an active cluster to deleting."

    let transaction (session: IClientSessionHandle) operation =
        if isNull (box session) then
            nullArg "session"

        if not session.IsInTransaction then
            invalidOp $"{operation} must run inside the logical resource transaction."
