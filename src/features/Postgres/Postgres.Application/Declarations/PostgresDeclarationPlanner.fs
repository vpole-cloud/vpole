namespace Pole.Features.Postgres.Application.Declarations

open System.Linq
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Features.StorageProfiles.Contracts

type PostgresDeclarationPlanner
    (
        resources: IMongoCollection<PostgresCluster>,
        reader: PostgresPlanningReader,
        loadPlacements: LoadResourceDeploymentPlanningSnapshot,
        fenceScope: FenceIsolationScopePlanningSnapshot,
        fenceTarget: FenceDeploymentTargetPlanningSnapshot,
        fenceCompute: FenceComputeProfilePlanningSnapshot,
        fenceStorage: FenceStorageProfilePlanningSnapshot,
        fencePlacements: FenceResourceDeploymentPlanningSnapshot,
        fenceCredentials: FenceConsumerSecretPlanningSnapshot,
        fenceAdoption: FenceManagedSecretAdoption
    ) =

    let currentVersion (current: PostgresCluster) (placement: ResourceDeploymentPlanningItem) =
        DeclarationVersionToken(
            [| DeclarationDocumentVersion(
                   DeclarationVersionKeys.Resource(PostgresDeclarations.Kind, current.Id),
                   current.Revision
               )
               DeclarationDocumentVersion(
                   DeclarationVersionKeys.PlanningFence(PostgresDeclarations.Kind, current.Id),
                   current.PlanningFence
               )
               DeclarationDocumentVersion(
                   ResourceDeploymentVersionKeys.Revision(placement.DeploymentId),
                   placement.Revision
               ) |]
        )

    let currentProjection phase (current: PostgresCluster) (placement: ResourceDeploymentPlanningItem) bootstrap =
        PostgresDeclarations.project
            phase
            { Identity =
                { ResourceGroupId = current.GroupId
                  IsolationScopeId = placement.IsolationScopeId
                  DeploymentTargetId = placement.DeploymentTargetId
                  Version = current.Spec.Version
                  DeploymentMode = current.Spec.DeploymentMode
                  BootstrapSecretId = bootstrap.SourceSecretId
                  BootstrapKey = bootstrap.SourceKey
                  Username = bootstrap.Username
                  StorageProfileId = current.Spec.StorageProfileId
                  Storage = placement.Storage }
              Instances = current.Spec.Instances
              StorageSizeGb = current.Spec.Storage
              FixedReplicas = placement.FixedReplicas
              Compute = placement.Compute }

    member _.ReadCurrentAsync
        (request: ReadCurrentDeclarationRequest, session: IClientSessionHandle, ct: CancellationToken)
        : Task<ReadCurrentDeclarationOutcome> =
        task {
            let! accepted =
                if isNull (box request.Operation) then
                    Task.FromResult<PostgresCluster>(Unchecked.defaultof<_>)
                else
                    resources
                        .Find(session, PostgresFilters.byId request.TenantId request.Operation.ResourceId)
                        .FirstOrDefaultAsync(ct)

            let! current =
                if not (isNull (box accepted)) then
                    Task.FromResult accepted
                else
                    resources
                        .Find(session, PostgresFilters.byName request.TenantId request.Address.Name)
                        .FirstOrDefaultAsync(ct)

            if isNull (box current) then
                return ReadCurrentDeclarationOutcome(CurrentDeclarationMissing())
            elif current.Name <> request.Address.Name then
                return ReadCurrentDeclarationOutcome(DeclarationReadBlocked(DeclarationIdentityChanged()))
            elif current.Lifecycle :? DeletingPostgresClusterLifecycle then
                return
                    ReadCurrentDeclarationOutcome(
                        PostgresDeclarations.blocked PostgresDeclarationError.ResourceDeleting
                    )
            else
                let! loaded =
                    loadPlacements.Invoke(
                        ResourceDeploymentPlanningRequest(
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            current.Id
                        ),
                        session,
                        ct
                    )

                match loaded.Value, PostgresPlanning.bootstrap current with
                | (:? ResourceDeploymentPlanningSnapshot as placements), Some bootstrap when
                    placements.Items.Count = 1
                    && placements.Items[0].State <> ResourceDeploymentPlanningState.Deleting
                    && placements.Items[0].ResourceGroupId = current.GroupId
                    && not (isNull placements.Items[0].Storage)
                    ->
                    let phase =
                        if isNull (box request.Operation) then
                            DeclarationPhase.Configure
                        else
                            request.Operation.Phase

                    let state =
                        if phase = DeclarationPhase.Prepare || not current.IsPrepared then
                            DeclarationConfigurationState.Configured
                        else
                            DeclarationConfigurationState.Prepared

                    return
                        ReadCurrentDeclarationOutcome(
                            CurrentDeclaration(
                                current.Id,
                                currentVersion current placements.Items[0],
                                currentProjection phase current placements.Items[0] bootstrap,
                                state
                            )
                        )
                | _, None ->
                    return
                        ReadCurrentDeclarationOutcome(
                            PostgresDeclarations.blocked PostgresDeclarationError.ExplicitAdoptionRequired
                        )
                | _ ->
                    return
                        ReadCurrentDeclarationOutcome(
                            PostgresDeclarations.blocked PostgresDeclarationError.PlacementUnavailable
                        )
        }

    member _.ProjectDesiredAsync
        (request: ProjectDesiredDeclarationRequest, session: IClientSessionHandle, ct: CancellationToken)
        : Task<ProjectDesiredDeclarationOutcome> =
        task {
            if isNull (box request.Operation) then
                return
                    ProjectDesiredDeclarationOutcome(
                        PostgresDeclarations.blocked PostgresDeclarationError.AcceptedSnapshotRequired
                    )
            else
                let! loaded = reader.ReadAsync(request.Operation, session, ct)

                match loaded with
                | Error failure -> return ProjectDesiredDeclarationOutcome(DeclarationReadBlocked failure)
                | Ok data ->
                    let spec = DeclarationJson.Read<PostgresDeclaration> request.Declaration.Spec
                    let catalog = data.Catalog

                    let projection =
                        PostgresDeclarations.project
                            request.Operation.Phase
                            { Identity =
                                { ResourceGroupId = catalog.Scope.ResourceGroupId
                                  IsolationScopeId = catalog.Scope.IsolationScopeId
                                  DeploymentTargetId = catalog.Target.Target.Id
                                  Version = spec.Version
                                  DeploymentMode = data.DeploymentMode
                                  BootstrapSecretId = data.BootstrapSecretId
                                  BootstrapKey = spec.BootstrapPassword.Key
                                  Username = spec.Username
                                  StorageProfileId = catalog.Storage.Storage.ProfileId
                                  Storage = PostgresResourceDeploymentMappings.storage catalog.Storage.Storage }
                              Instances = PostgresDeclarations.instances spec
                              StorageSizeGb = spec.Storage.SizeGb
                              FixedReplicas = PostgresDeclarations.instances spec
                              Compute = PostgresResourceDeploymentMappings.compute catalog.Compute.Compute }

                    return
                        ProjectDesiredDeclarationOutcome(
                            DeclarationProjection(
                                projection.Identity,
                                projection.Configuration,
                                DeclarationVersionToken(PostgresPlanning.versions data)
                            )
                        )
        }

    member _.FenceAsync
        (request: FenceDeclarationRequest, session: IClientSessionHandle, ct: CancellationToken)
        : Task<FenceDeclarationOutcome> =
        task {
            let! loaded = reader.ReadAsync(request.Operation, session, ct)

            match loaded with
            | Error failure -> return FenceDeclarationOutcome(DeclarationReadBlocked failure)
            | Ok data ->
                let versionsMatch =
                    PostgresPlanning.versions data
                    |> Array.forall request.Expected.Documents.Contains

                let expectedRoot =
                    request.Expected.Documents
                    |> Seq.tryFind (fun version ->
                        version.DocumentId = DeclarationVersionKeys.Resource(
                            PostgresDeclarations.Kind,
                            request.Operation.ResourceId
                        ))

                let! rootFenced =
                    match data.Current, expectedRoot with
                    | None, None -> Task.FromResult true
                    | Some current, Some expected when
                        current.Revision = expected.Revision
                        && request.Expected.Documents.Contains(
                            DeclarationDocumentVersion(
                                DeclarationVersionKeys.PlanningFence(PostgresDeclarations.Kind, current.Id),
                                current.PlanningFence
                            )
                        )
                        ->
                        task {
                            let! fenced =
                                resources.UpdateOneAsync(
                                    session,
                                    PostgresFilters.atRevision current,
                                    Builders<PostgresCluster>.Update.Inc((fun value -> value.PlanningFence), 1L),
                                    cancellationToken = ct
                                )

                            return fenced.ModifiedCount = 1L
                        }
                    | _ -> Task.FromResult false

                if not versionsMatch || not rootFenced then
                    return FenceDeclarationOutcome(DeclarationPreconditionsChanged())
                else
                    let! scope = fenceScope.Invoke(data.Catalog.Scope, session, ct)
                    let! target = fenceTarget.Invoke(data.Catalog.Target, session, ct)
                    let! compute = fenceCompute.Invoke(data.Catalog.Compute, session, ct)
                    let! storage = fenceStorage.Invoke(data.Catalog.Storage, session, ct)

                    let! placements =
                        match data.Placements with
                        | None -> Task.FromResult true
                        | Some value ->
                            task {
                                let! result = fencePlacements.Invoke(value, session, ct)
                                return result.Value :? ResourceDeploymentPlanningFenced
                            }

                    let! credentials =
                        match data.Credentials with
                        | None -> Task.FromResult true
                        | Some value ->
                            task {
                                let! result = fenceCredentials.Invoke(value, session, ct)
                                return result.Value :? ConsumerSecretPlanningFenced
                            }

                    let! adoptedCredential =
                        match data.Adoption with
                        | None -> Task.FromResult true
                        | Some value ->
                            task {
                                let! result = fenceAdoption.Invoke(value, session, ct)
                                return result.Value :? ManagedSecretAdoptionFenced
                            }

                    if
                        (scope.Value :? IsolationScopePlanningFenced)
                        && (target.Value :? DeploymentTargetPlanningFenced)
                        && (compute.Value :? ComputeProfilePlanningFenced)
                        && (storage.Value :? StorageProfilePlanningFenced)
                        && placements
                        && credentials
                        && adoptedCredential
                    then
                        return FenceDeclarationOutcome(DeclarationFenced())
                    else
                        return FenceDeclarationOutcome(DeclarationPreconditionsChanged())
        }
