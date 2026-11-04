namespace Pole.Features.Postgres.Application.Persistence.Declarative

open System
open System.Linq
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open MongoDB.Driver
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Lifecycle
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceConnections.Contracts
open Pole.Features.ResourceDeployments.Contracts

type PostgresDeclarationLifecycle(
    resources : IMongoCollection<PostgresCluster>,
    loadPlacements : LoadResourceDeploymentPlanningSnapshot,
    fencePlacements : FenceResourceDeploymentPlanningSnapshot,
    beginDeletion : BeginPostgresClusterDeletion,
    deletePlacement : RequestResourceDeploymentDeletion,
    releaseQuota : IQuotaUsageRelease,
    git : IGitOpsMutations,
    guard : IGitLifecyclePlanGuard,
    loadCascade : LoadResourceConnectionCascadePlanning,
    clock : TimeProvider) =

    let placementVersions (item : ResourceDeploymentPlanningItem) (current : PostgresCluster) =
        if not (current.Lifecycle :? DeletingPostgresClusterLifecycle)
           || item.State <> ResourceDeploymentPlanningState.Deleting then
            [| DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}/DesiredGeneration",
                                          item.DesiredGeneration)
               DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}/ResourceRevision",
                                          item.ResourceRevision) |]
        else
            let bootstrap = PostgresPlanning.bootstrap current
            let ingress =
                if isNull (box item.Ingress) then null
                else box {| IngressClassName = item.Ingress.IngressClassName
                            ClusterIssuerName = item.Ingress.ClusterIssuerName
                            PublicBaseDomain = item.Ingress.PublicBaseDomain
                            Hostnames = item.Ingress.Hostnames
                                        |> Seq.sortWith (fun left right ->
                                            System.StringComparer.Ordinal.Compare(left, right))
                                        |> Seq.toArray |}
            let intent =
                {| OwnerGroupId = current.GroupId
                   Spec = current.Spec
                   Provisioning = current.Provisioning.GetType().Name
                   BootstrapSecretId = bootstrap |> Option.map _.SourceSecretId |> Option.defaultValue null
                   BootstrapKey = bootstrap |> Option.map _.SourceKey |> Option.defaultValue null
                   CredentialId = bootstrap |> Option.map _.CredentialId |> Option.defaultValue null
                   Username = bootstrap |> Option.map _.Username |> Option.defaultValue null
                   ResourceGroupId = item.ResourceGroupId
                   IsolationScopeId = item.IsolationScopeId
                   DeploymentTargetId = item.DeploymentTargetId
                   FixedReplicas = item.FixedReplicas
                   ComputeProfileId = item.Compute.ComputeProfileId
                   ComputeProfileRevision = item.Compute.ComputeProfileRevision
                   Compute = PostgresDeclarations.computeProjection item.Compute
                   Storage = item.Storage
                   Ingress = ingress
                   DeploymentTargetConfigurationRevision = item.DeploymentTargetConfigurationRevision
                   DesiredGeneration = item.DesiredGeneration
                   ResourceRevision = item.ResourceRevision |}
            let hash = System.Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent)))
            [| DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}/DeleteIntent/{hash}",
                                          item.DesiredGeneration) |]

    let versions (current : PostgresCluster) (placements : LoadResourceDeploymentPlanningSnapshotOutcome) =
        DeclarationVersionToken(
            [| DeclarationDocumentVersion($"PostgresCluster/{current.Id}", current.Revision)
               DeclarationDocumentVersion($"PostgresCluster/{current.Id}/PlanningFence", current.PlanningFence)
               match placements.Value with
               | :? ResourceDeploymentPlanningSnapshot as placed ->
                   for item in placed.Items do
                       yield! placementVersions item current
               | _ -> () |])

    let same (left : DeclarationVersionToken) (right : DeclarationVersionToken) =
        left.Documents.OrderBy(fun item -> item.DocumentId).SequenceEqual(
            right.Documents.OrderBy(fun item -> item.DocumentId))

    member _.ReadAsync(request : ReadLifecycleResourceRequest, session : IClientSessionHandle, ct : CancellationToken) =
        task {
            let! current = resources.Find(session, PostgresFilters.byId request.TenantId request.Resource.ResourceId)
                               .FirstOrDefaultAsync(ct)
            if isNull (box current) then
                return ReadLifecycleResourceOutcome(GitLifecycleResourceAbsent())
            elif request.Resource.Address.Kind <> PostgresDeclarations.Kind ||
                 current.Name <> request.Resource.Address.Name then
                return ReadLifecycleResourceOutcome(DeclarationReadBlocked(DeclarationIdentityChanged()))
            else
                let! placements = loadPlacements.Invoke(ResourceDeploymentPlanningRequest(request.TenantId,
                    PostgresResourceDeployment.ResourceType, current.Id), session, ct)
                let presence =
                    if current.Lifecycle :? DeletingPostgresClusterLifecycle then GitLifecyclePresence.Deleting
                    else GitLifecyclePresence.Active
                return ReadLifecycleResourceOutcome(GitLifecycleResourceSnapshot(request.Resource,
                    versions current placements, presence, GitLifecycleProtection.ProtectedData,
                    GitLifecycleVersionSemantics.ManagedConfiguration))
        }

    member this.RemoveAsync(
        request : RemoveLifecycleResourceRequest, session : IClientSessionHandle, ct : CancellationToken) =
        task {
            let! read = this.ReadAsync(ReadLifecycleResourceRequest(request.Authority.TenantId, request.Resource),
                session, ct)
            match read.Value with
            | :? GitLifecycleResourceAbsent -> return RemoveLifecycleResourceOutcome(GitLifecycleResourceRemoved())
            | :? DeclarationReadBlocked as blocked -> return RemoveLifecycleResourceOutcome(blocked)
            | :? GitLifecycleResourceSnapshot as snapshot when not (same snapshot.Version request.Expected) ->
                return RemoveLifecycleResourceOutcome(DeclarationPreconditionsChanged())
            | :? GitLifecycleResourceSnapshot as snapshot ->
                let! authorized = git.AuthorizeAsync(AuthorizeGitResourceMutationRequest(request.Authority.TenantId,
                    PostgresDeclarations.Kind, request.Resource.ResourceId, Lifecycle = request.Authority,
                    LifecycleEffect = Nullable(GitLifecycleMutationEffect.Remove)), session, ct)
                if not authorized then
                    return RemoveLifecycleResourceOutcome(StaleGitExecution())
                elif snapshot.Presence = GitLifecyclePresence.Deleting then
                    return RemoveLifecycleResourceOutcome(GitLifecycleRemovalRequested())
                else
                    let! approved = guard.ReadScopeAsync(request.Authority, session, ct)
                    match approved.Value with
                    | :? StaleGitExecution as stale -> return RemoveLifecycleResourceOutcome(stale)
                    | :? GitLifecycleApprovedScope as scope ->
                        let reviewed =
                            scope.Resources
                            |> Seq.filter (fun item ->
                                item.Resource.Address.Kind = "ResourceConnection" &&
                                (item.Action = GitLifecycleAction.Remove || item.Action = GitLifecycleAction.Replace))
                            |> Seq.map (fun (item : GitLifecycleApprovedResource) ->
                                ResourceConnectionPlanningVersion.FromDocuments(
                                item.Resource.ResourceId,
                                item.Expected.Documents.ToDictionary((fun value -> value.DocumentId),
                                    (fun value -> value.Revision)))
                                |> Option.ofObj
                                |> Option.defaultWith (fun () ->
                                    invalidOp "An approved connection requires its exact planning version."))
                            |> Seq.toArray
                        let! affected = loadCascade.Invoke(LoadResourceConnectionCascadePlanningRequest(
                            request.Authority.TenantId, PostgresResourceDeployment.ResourceType,
                            request.Resource.ResourceId), session, ct)
                        let cascadesMatch = affected |> Seq.forall (fun item -> reviewed |> Array.contains item)
                        let! current = resources.Find(session,
                            PostgresFilters.byId request.Authority.TenantId request.Resource.ResourceId).SingleAsync(ct)
                        let! placements = loadPlacements.Invoke(ResourceDeploymentPlanningRequest(current.TenantId,
                            PostgresResourceDeployment.ResourceType, current.Id), session, ct)
                        match placements.Value with
                        | _ when not cascadesMatch ->
                            return RemoveLifecycleResourceOutcome(DeclarationReadBlocked(GitLifecycleApprovalStale()))
                        | :? ResourceDeploymentPlanningSnapshot as placed
                            when placed.Items.Count = 1 ->
                            let! fenced = fencePlacements.Invoke(placed, session, ct)
                            if not (fenced.Value :? ResourceDeploymentPlanningFenced) then
                                return RemoveLifecycleResourceOutcome(DeclarationPreconditionsChanged())
                            else
                                let! removed = beginDeletion.Invoke(
                                    { Current = current
                                      Replacement = current.BeginDeletion(clock.GetUtcNow().UtcDateTime)
                                      Authority = request.Authority
                                      ReviewedConnections = affected }, session, ct)
                                match removed with
                                | PostgresError conflict when conflict.Reason =
                                    PostgresMutationConflictReason.SecretBindingsRequireRemoval ->
                                    return RemoveLifecycleResourceOutcome(
                                        DeclarationReadBlocked(GitLifecycleDependencyRetained()))
                                | PostgresError _ ->
                                    return RemoveLifecycleResourceOutcome(DeclarationPreconditionsChanged())
                                | PostgresSuccess () ->
                                    let compute = placed.Items[0].Compute
                                    let quota =
                                        PostgresQuotaInput.create compute.Requests.CpuMillis
                                            compute.Requests.MemoryMi (int64 current.Spec.Instances)
                                    do! releaseQuota.ReleaseAsync(
                                        ReleaseQuotaUsageRequest<PostgresQuotaInput>(current.TenantId, quota),
                                        session, ct)
                                    let! deployment = deletePlacement.Invoke(ResourceDeploymentMutationRequest(
                                        current.TenantId, PostgresResourceDeployment.ResourceType, current.Id,
                                        ComputeProfileReference(compute.ComputeProfileId,
                                            compute.ComputeProfileRevision)),
                                        session, ct)
                                    match deployment.Value with
                                    | :? PlacementSummaryDto ->
                                        return RemoveLifecycleResourceOutcome(GitLifecycleRemovalRequested())
                                    | _ -> return RemoveLifecycleResourceOutcome(DeclarationPreconditionsChanged())
                        | _ -> return RemoveLifecycleResourceOutcome(
                            PostgresDeclarations.blocked PostgresDeclarationError.PlacementUnavailable)
                    | _ -> return invalidOp "Unsupported PostgreSQL lifecycle scope outcome."
            | _ -> return invalidOp "Unsupported PostgreSQL lifecycle read outcome."
        }

    member _.ReadDependenciesAsync(
        request : ReadLifecycleDependenciesRequest, session : IClientSessionHandle, ct : CancellationToken) =
        task {
            let result = ResizeArray<GitLifecycleDependency>()
            if request.Resource.Address.Kind = "ResourceGroup" || request.Resource.Address.Kind = "IsolationScope" then
                let! current = resources.Find(session, PostgresFilters.visibleToTenant request.TenantId)
                                   .SortBy(fun item -> item.Id).ToListAsync(ct)
                for item in current do
                    let! placements = loadPlacements.Invoke(ResourceDeploymentPlanningRequest(request.TenantId,
                        PostgresResourceDeployment.ResourceType, item.Id), session, ct)
                    let selected =
                        if request.Resource.Address.Kind = "ResourceGroup" then
                            item.GroupId = request.Resource.ResourceId
                        else
                            match placements.Value with
                            | :? ResourceDeploymentPlanningSnapshot as placed ->
                                placed.Items |> Seq.exists (fun value ->
                                    value.IsolationScopeId = request.Resource.ResourceId)
                            | _ -> false
                    if selected then
                        result.Add(GitLifecycleDependency(GitLifecycleResourceReference(
                            ResourceAddress(PostgresDeclarations.Kind, item.Name), item.Id), request.Resource,
                            versions item placements))
            return result :> IReadOnlyList<GitLifecycleDependency>
        }

    member this.FenceDependenciesAsync(
        request : FenceLifecycleDependenciesRequest, session : IClientSessionHandle, ct : CancellationToken) =
        task {
            let! current = this.ReadDependenciesAsync(ReadLifecycleDependenciesRequest(request.TenantId,
                request.Resource), session, ct)
            let matches =
                current.Count = request.Expected.Count &&
                (current |> Seq.forall (fun item ->
                    request.Expected |> Seq.exists (fun expected ->
                        expected.Dependent = item.Dependent && expected.Target = item.Target &&
                        same expected.Version item.Version)))
            if not matches then
                return FenceDeclarationOutcome(DeclarationPreconditionsChanged())
            else
                let mutable fenced = true
                for item in current do
                    let! root = resources.Find(session,
                        PostgresFilters.byId request.TenantId item.Dependent.ResourceId).SingleAsync(ct)
                    let! result = resources.UpdateOneAsync(session, PostgresFilters.atRevision root,
                        Builders<PostgresCluster>.Update.Inc((fun value -> value.PlanningFence), 1L),
                        cancellationToken = ct)
                    if result.ModifiedCount <> 1L then fenced <- false
                return if fenced then FenceDeclarationOutcome(DeclarationFenced())
                       else FenceDeclarationOutcome(DeclarationPreconditionsChanged())
        }
