namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading
open MongoDB.Bson
open MongoDB.Driver
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Workloads

[<RequireQualifiedAccess>]
module PostgresDatabaseIdentity =
    let reference (claim: PostgresManagedDatabase) =
        PostgresManagedDatabaseReference(claim.Id, claim.ClusterId, claim.Database, claim.Username)

    let credentialOwner (claim: PostgresManagedDatabase) =
        ManagedSecretOwner(PostgresResourceDeployment.ResourceType.Value, claim.ClusterId, $"database:{claim.Id}")

    let ownerFilter (tenant: string) (owner: PostgresManagedDatabaseOwner) =
        PostgresDatabaseFilters.owner tenant (ResourceTypeId.Create(owner.ResourceType).Value) owner.ResourceId


type MongoClaimPostgresManagedDatabase
    (clusters: IMongoCollection<PostgresCluster>, claims: IMongoCollection<PostgresManagedDatabase>,
     loadPlanning: LoadResourceDeploymentPlanningSnapshot, fencePlanning: FenceResourceDeploymentPlanningSnapshot,
     loadPlacements: LoadResourceDeploymentConnectionPlacements, admitScope: AdmitResourceScopeReference,
     fenceResource: FenceRegisteredResource, prepareSecret: PrepareManagedSecret, createSecret: CreateBoundManagedSecret,
     clock: TimeProvider) =

    member _.InvokeAsync(request: ClaimPostgresManagedDatabaseRequest, session: IClientSessionHandle, ct: CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "Managed PostgreSQL database admission"
            PostgresPersistenceGuards.identity request.TenantId request.PostgresClusterId
            let consumerType = ResourceTypeId.Create(request.Owner.ResourceType)
            if not (ObjectId.TryParse(request.Owner.ResourceId) |> fst) ||
               String.IsNullOrWhiteSpace request.SourceComponent || request.SourceComponent.Length > 63 ||
               not (System.Text.RegularExpressions.Regex.IsMatch(request.SourceComponent, "^[a-z0-9][a-z0-9_.-]*$")) then
                invalidArg "request" "A stable consumer identity and exact workload component are required."

            let! previous = claims.Find(session, PostgresDatabaseIdentity.ownerFilter request.TenantId request.Owner).FirstOrDefaultAsync(ct)
            if not (isNull (box previous)) then
                return
                    if previous.IsActive && previous.ClusterId = request.PostgresClusterId &&
                       previous.Source.ResourceGroupId = request.Placement.ResourceGroupId &&
                       previous.Source.IsolationScopeId = request.Placement.IsolationScopeId &&
                       previous.Source.DeploymentTargetId = request.Placement.DeploymentTargetId &&
                       previous.Source.Component = request.SourceComponent then
                        ClaimPostgresManagedDatabaseOutcome(PostgresDatabaseIdentity.reference previous)
                    else ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
            else
                let! cluster = clusters.Find(session, PostgresFilters.byId request.TenantId request.PostgresClusterId).FirstOrDefaultAsync(ct)
                if isNull (box cluster) || not (cluster.Lifecycle :? ActivePostgresClusterLifecycle) || cluster.IsPrepared then
                    return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseUnavailable())
                elif cluster.Spec.ReplicationMode <> PostgresReplicationMode.SynchronousHa || cluster.Spec.Instances < 3 then
                    return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseIneligible(PostgresManagedDatabaseIneligibility.SynchronousHaRequired))
                else
                    let! pgPlanning = loadPlanning.Invoke(ResourceDeploymentPlanningRequest(request.TenantId, PostgresResourceDeployment.ResourceType, cluster.Id), session, ct)
                    let! sourcePlanning = loadPlanning.Invoke(ResourceDeploymentPlanningRequest(request.TenantId, consumerType, request.Owner.ResourceId), session, ct)
                    let! pgPlacement = loadPlacements.Invoke(LoadResourceDeploymentConnectionPlacementsRequest(request.TenantId, PostgresResourceDeployment.ResourceType, cluster.Id), session, ct)
                    match pgPlanning.Value, sourcePlanning.Value with
                    | (:? ResourceDeploymentPlanningSnapshot as pg), (:? ResourceDeploymentPlanningSnapshot as source)
                        when pg.Items.Count = 1 && source.Items.Count = 1 && pgPlacement.Count = 1 &&
                             pg.Items[0].State = ResourceDeploymentPlanningState.Active &&
                             source.Items[0].State = ResourceDeploymentPlanningState.Active && not pgPlacement[0].DeletionRequested &&
                             source.Items[0].ResourceGroupId = request.Placement.ResourceGroupId &&
                             source.Items[0].IsolationScopeId = request.Placement.IsolationScopeId ->
                        let target = pgPlacement[0]
                        if target.DeploymentTargetId <> request.Placement.DeploymentTargetId ||
                           source.Items[0].DeploymentTargetId <> target.DeploymentTargetId then
                            return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseIneligible(PostgresManagedDatabaseIneligibility.DeploymentTargetMismatch))
                        else
                            let! scope = admitScope.Invoke(AdmitResourceScopeReferenceRequest(
                                request.TenantId, request.Placement.ResourceGroupId, request.Placement.IsolationScopeId,
                                ResourceScopeConsumerReference(consumerType, request.Owner.ResourceId)), session, ct)
                            let! sourceFence = fencePlanning.Invoke(source, session, ct)
                            let! pgFence = fencePlanning.Invoke(pg, session, ct)
                            let! owner = fenceResource.Invoke(FenceRegisteredResourceRequest(request.TenantId, consumerType, request.Owner.ResourceId), session, ct)
                            if scope.Value :? ResourceScopeReferenceUnavailable ||
                               not (sourceFence.Value :? ResourceDeploymentPlanningFenced) ||
                               not (pgFence.Value :? ResourceDeploymentPlanningFenced) || isNull owner then
                                return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
                            else
                                let claim = PostgresManagedDatabase.Create(
                                    request.TenantId, cluster.Id,
                                    { ResourceType = consumerType.Value; ResourceId = request.Owner.ResourceId },
                                    { ResourceGroupId = request.Placement.ResourceGroupId; IsolationScopeId = request.Placement.IsolationScopeId;
                                      DeploymentTargetId = target.DeploymentTargetId; Component = request.SourceComponent },
                                    { DeploymentId = target.DeploymentId; DeploymentTargetId = target.DeploymentTargetId;
                                      RuntimeCellId = target.RuntimeCellId; KubernetesNamespace = target.KubernetesNamespace },
                                    ObjectId.GenerateNewId().ToString(), clock.GetUtcNow().UtcDateTime)
                                let values = Dictionary<string, string>()
                                values["username"] <- claim.Username
                                values["password"] <- Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
                                let! prepared = prepareSecret.Invoke(PrepareManagedSecretRequest(
                                    claim.TenantId, claim.CredentialId, PostgresDatabaseIdentity.credentialOwner claim,
                                    $"pg-db-{claim.Id}-credentials", 1L, values, ManagedSecretKubernetesType.BasicAuthentication,
                                    ManagedSecretSharingPolicy.OwnerOnly), ct)
                                match prepared.Value with
                                | :? ManagedSecretEncryptionUnavailable ->
                                    return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseProtectionUnavailable())
                                | :? PreparedManagedSecret as credential ->
                                    let! fenced = clusters.UpdateOneAsync(session, PostgresFilters.activeAtRevision cluster,
                                                    Builders<PostgresCluster>.Update.Inc((fun item -> item.PlanningFence), 1L), cancellationToken = ct)
                                    if fenced.ModifiedCount <> 1L then
                                        return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
                                    else
                                        let! secret = createSecret.Invoke(CreateBoundManagedSecretRequest(
                                            credential, cluster.GroupId, pg.Items[0].IsolationScopeId, target.DeploymentId), session, ct)
                                        if not (secret.Value :? ManagedSecretMutationSucceeded) then
                                            return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
                                        else
                                            try
                                                do! claims.InsertOneAsync(session, claim, cancellationToken = ct)
                                                return ClaimPostgresManagedDatabaseOutcome(PostgresDatabaseIdentity.reference claim)
                                            with :? MongoWriteException as error when error.WriteError.Category = ServerErrorCategory.DuplicateKey ->
                                                return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseChanged())
                                | _ -> return invalidOp "Unknown managed credential preparation outcome."
                    | _ ->
                        return ClaimPostgresManagedDatabaseOutcome(PostgresManagedDatabaseIneligible(PostgresManagedDatabaseIneligibility.ConsumerPlacementUnavailable))
        }
