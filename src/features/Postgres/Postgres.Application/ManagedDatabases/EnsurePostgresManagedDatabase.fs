namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Collections.Immutable
open System.Net
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open k8s
open k8s.Autorest
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Workloads

type private ManagedDatabasePlacement =
    { Claim: PostgresManagedDatabase
      Cluster: PostgresCluster
      Target: ResourceDeploymentRuntimePlacementDto
      Source: ResourceDeploymentRuntimePlacementDto }

type private ManagedDatabaseAuthority =
    { Placement: ManagedDatabasePlacement
      Client: IKubernetes
      ClusterUid: string
      NativeGeneration: int64
      RuntimeEpoch: string
      Projection: SecretProjectionReference
      CredentialResourceVersion: string }

type ReconcilePostgresManagedDatabase
    (store: PostgresManagedDatabaseStateStore, clusters: IMongoCollection<PostgresCluster>,
     loadPlacements: LoadResourceDeploymentRuntimePlacements, clients: IKubernetesClientProvider,
     ensureProjection: EnsureManagedSecretProjection, readKey: ReadManagedSecretKey,
     native: PostgresManagedDatabaseNative, probe: PostgresManagedDatabaseProbe) =

    let pending reason = EnsurePostgresManagedDatabaseOutcome(PostgresManagedDatabasePending(reason))
    let unavailable () = EnsurePostgresManagedDatabaseOutcome(PostgresManagedDatabaseUnavailable())
    let owner (claim: PostgresManagedDatabase) =
        PostgresManagedDatabaseOwner(claim.Owner.ResourceType, claim.Owner.ResourceId)

    let bind (next: 'a -> Task<Result<'b, EnsurePostgresManagedDatabaseOutcome>>)
             (current: Task<Result<'a, EnsurePostgresManagedDatabaseOutcome>>) =
        task {
            let! result = current
            match result with
            | Error failure -> return Error failure
            | Ok accepted -> return! next accepted
        }

    let isCurrent (claim: PostgresManagedDatabase) ct =
        task {
            let! latest = store.LoadAsync(claim.TenantId, owner claim, ct)
            return not (isNull (box latest)) && latest.IsActive &&
                   latest.Id = claim.Id && latest.Revision = claim.Revision
        }

    let loadPlacement (request: EnsurePostgresManagedDatabaseRequest) ct =
        task {
            let! claim = store.LoadAsync(request.TenantId, request.Owner, ct)
            if isNull (box claim) || not claim.IsActive then return Error(unavailable ())
            else
                let! cluster = clusters.Find(PostgresFilters.byId claim.TenantId claim.ClusterId).FirstOrDefaultAsync(ct)
                if isNull (box cluster) || not (cluster.Lifecycle :? ActivePostgresClusterLifecycle) ||
                   cluster.IsPrepared || cluster.Spec.ReplicationMode <> PostgresReplicationMode.SynchronousHa ||
                   cluster.Spec.Instances < 3 then return Error(unavailable ())
                else
                    let! pg = loadPlacements.Invoke(LoadResourceDeploymentRuntimePlacementsRequest(
                                    claim.TenantId, PostgresResourceDeployment.ResourceType, [| claim.ClusterId |]), ct)
                    let! source = loadPlacements.Invoke(LoadResourceDeploymentRuntimePlacementsRequest(
                                    claim.TenantId, ResourceTypeId.Create(claim.Owner.ResourceType),
                                    [| claim.Owner.ResourceId |]), ct)
                    let live resourceId (values: seq<ResourceDeploymentRuntimePlacementDto>) =
                        values
                        |> Seq.filter(fun item ->
                            item.ResourceId = resourceId &&
                            item.DesiredOperation <> ResourceDeploymentDesiredOperation.Delete)
                        |> Seq.toArray
                    let targets = live claim.ClusterId pg
                    let sources = live claim.Owner.ResourceId source
                    if targets.Length <> 1 || sources.Length <> 1 then
                        return Error(pending PostgresManagedDatabasePendingReason.PlacementUnavailable)
                    else
                        let target = targets[0]
                        let consumer = sources[0]
                        let targetMatches =
                            target.DeploymentId = claim.Target.DeploymentId &&
                            target.RuntimeCellId = claim.Target.RuntimeCellId &&
                            target.KubernetesNamespace = claim.Target.KubernetesNamespace &&
                            target.DeploymentTargetId = claim.Target.DeploymentTargetId
                        let sourceMatches =
                            consumer.DeploymentTargetId = claim.Target.DeploymentTargetId &&
                            consumer.IsolationScopeId = claim.Source.IsolationScopeId &&
                            not (String.IsNullOrWhiteSpace consumer.RuntimeCellId) &&
                            not (String.IsNullOrWhiteSpace consumer.KubernetesNamespace)
                        return
                            if not targetMatches || not sourceMatches then
                                Error(pending PostgresManagedDatabasePendingReason.PlacementUnavailable)
                            else Ok { Claim = claim; Cluster = cluster; Target = target; Source = consumer }
        }

    let resolveAuthority (placement: ManagedDatabasePlacement) ct =
        task {
            let claim = placement.Claim
            let! client = clients.GetAsync(claim.Target.DeploymentTargetId, ct)
            let! identity = native.ReadReadyClusterUidAsync(client, claim, placement.Cluster, ct)
            match identity with
            | None -> return Error(pending PostgresManagedDatabasePendingReason.PlacementUnavailable)
            | Some(uid, generation, epoch) ->
                let! result = ensureProjection.Invoke(EnsureManagedSecretProjectionRequest(
                                claim.TenantId, PostgresDatabaseIdentity.credentialOwner claim,
                                claim.Target.DeploymentId), ct)
                match result.Value with
                | :? ManagedSecretUnavailable ->
                    return Error(pending PostgresManagedDatabasePendingReason.CredentialProjectionPending)
                | :? SecretProjectionReference as projection ->
                    try
                        let! secret = client.CoreV1.ReadNamespacedSecretAsync(
                                        projection.KubernetesName, claim.Target.KubernetesNamespace, cancellationToken = ct)
                        if String.IsNullOrWhiteSpace secret.Metadata.ResourceVersion then
                            return Error(pending PostgresManagedDatabasePendingReason.CredentialProjectionPending)
                        else
                            return Ok { Placement = placement; Client = client; ClusterUid = uid
                                        NativeGeneration = generation; RuntimeEpoch = epoch; Projection = projection
                                        CredentialResourceVersion = secret.Metadata.ResourceVersion }
                    with :? HttpOperationException as error when
                        not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound ->
                        return Error(pending PostgresManagedDatabasePendingReason.CredentialProjectionPending)
                | _ -> return invalidOp "Unknown managed database projection outcome."
        }

    let applyNative (authority: ManagedDatabaseAuthority) ct =
        task {
            let claim = authority.Placement.Claim
            let client = authority.Client
            let current = isCurrent claim
            let role = PostgresManagedDatabaseManifests.role claim authority.Projection.KubernetesName authority.ClusterUid
            let database = PostgresManagedDatabaseManifests.database claim authority.ClusterUid
            let policy = PostgresManagedDatabaseManifests.networkPolicy claim [| authority.Placement.Source |]
            let! policyApplied = native.ApplyPolicyAsync(client, claim, policy, current, ct)
            if not policyApplied then return Error(pending PostgresManagedDatabasePendingReason.NativeOwnershipConflict)
            else
                let! roleApplied = native.ApplyCustomAsync(client, claim, "databaseroles", role, current, ct)
                if not roleApplied then return Error(pending PostgresManagedDatabasePendingReason.NativeOwnershipConflict)
                else
                    let! databaseApplied = native.ApplyCustomAsync(client, claim, "databases", database, current, ct)
                    if not databaseApplied then return Error(pending PostgresManagedDatabasePendingReason.NativeOwnershipConflict)
                    else
                        let! actualRole = native.ReadCustomAsync(client, claim, "databaseroles", ct)
                        let! actualDatabase = native.ReadCustomAsync(client, claim, "databases", ct)
                        let roleReady = actualRole |> Option.exists(fun value ->
                            native.Ready(claim, value, role, Some authority.CredentialResourceVersion))
                        let databaseReady = actualDatabase |> Option.exists(fun value ->
                            native.Ready(claim, value, database, None))
                        return
                            if not roleReady then Error(pending PostgresManagedDatabasePendingReason.RolePending)
                            elif not databaseReady then Error(pending PostgresManagedDatabasePendingReason.DatabasePending)
                            else Ok authority
        }

    let verifyAndRead (authority: ManagedDatabaseAuthority) ct =
        task {
            let placement = authority.Placement
            let claim = placement.Claim
            let! verified = probe.VerifyAsync(
                                authority.Client, claim, placement.Cluster, placement.Target,
                                authority.ClusterUid, authority.NativeGeneration, authority.RuntimeEpoch, authority.CredentialResourceVersion,
                                authority.Projection, isCurrent claim, ct)
            if not verified then return Error(pending PostgresManagedDatabasePendingReason.DatabasePending)
            else
                let! identity = native.ReadReadyClusterUidAsync(authority.Client, claim, placement.Cluster, ct)
                let! authorized = isCurrent claim ct
                if not authorized || identity <> Some(authority.ClusterUid, authority.NativeGeneration, authority.RuntimeEpoch) then
                    return Error(unavailable ())
                else
                    let! password = readKey.Invoke(ReadManagedSecretKeyRequest(
                                        claim.TenantId, PostgresDatabaseIdentity.credentialOwner claim, "password"), ct)
                    match password.Value with
                    | :? ManagedSecretEncryptionUnavailable ->
                        return Error(EnsurePostgresManagedDatabaseOutcome(PostgresManagedDatabaseProtectionUnavailable()))
                    | :? ManagedSecretUnavailable -> return Error(unavailable ())
                    | :? ManagedSecretKey as value ->
                        let! stillAuthorized = isCurrent claim ct
                        if not stillAuthorized then return Error(unavailable ())
                        else
                            let host = $"pg-{claim.ClusterId}-rw.{claim.Target.KubernetesNamespace}.svc.cluster.local"
                            let runtime = PostgresManagedDatabaseRuntime(
                                            host, 5432, claim.Target.KubernetesNamespace, claim.Target.DeploymentTargetId,
                                            claim.Target.RuntimeCellId, claim.Target.DeploymentId,
                                            (PostgresManagedDatabaseManifests.podLabels claim).ToImmutableDictionary())
                            return Ok(PostgresManagedDatabaseReady(
                                        PostgresDatabaseIdentity.reference claim, runtime,
                                        PostgresManagedDatabaseCredential(claim.Username, value.Value)))
                    | _ -> return invalidOp "Unknown managed database credential outcome."
        }

    member _.InvokeAsync(request: EnsurePostgresManagedDatabaseRequest, ct: CancellationToken) =
        task {
            try
                let! result =
                    loadPlacement request ct
                    |> bind (fun placement -> resolveAuthority placement ct)
                    |> bind (fun authority -> applyNative authority ct)
                    |> bind (fun authority -> verifyAndRead authority ct)
                return match result with
                       | Ok ready -> EnsurePostgresManagedDatabaseOutcome(ready)
                       | Error failure -> failure
            with :? DeploymentTargetUnavailableException ->
                return pending PostgresManagedDatabasePendingReason.PlacementUnavailable
        }
