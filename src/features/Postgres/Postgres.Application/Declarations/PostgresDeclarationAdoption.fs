namespace Pole.Features.Postgres.Application.Declarations

open System.Threading
open MongoDB.Driver
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Outbox.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Domain
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.StorageProfiles.Contracts

type PostgresDeclarationAdoption
    (
        resources: IMongoCollection<PostgresCluster>,
        reader: PostgresPlanningReader,
        planner: PostgresDeclarationPlanner,
        git: IGitOpsMutations,
        outbox: StoreOutboxWrites,
        resolveTarget: ResolveNamedDeploymentTarget,
        resolveCompute: ResolveNamedComputeProfile,
        resolveStorage: ResolveNamedStorageProfile,
        loadCompute: ResolveComputeProfilePlanningSnapshot,
        loadStorage: ResolveStorageProfilePlanningSnapshot
    ) =

    let references (request: ReadPreviewReferencesRequest) session ct =
        task {
            let spec = DeclarationJson.Read<PostgresDeclaration> request.Consumer.Spec

            let targetAddress =
                ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget)

            let computeAddress =
                ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name)

            let storageAddress = ResourceAddress("StorageProfile", spec.Storage.Profile.Name)

            let matches address id =
                request.References
                |> Seq.forall (fun item -> item.Address <> address || item.ResourceId = id)

            let! target =
                resolveTarget.Invoke(
                    NamedDeploymentTargetRequest(
                        request.TenantId,
                        PostgresResourceDeployment.ResourceType,
                        targetAddress.Name
                    ),
                    ct
                )

            match target.Value with
            | :? DeploymentTargetLookupDto as target when matches targetAddress target.Id ->
                let! compute =
                    resolveCompute.Invoke(
                        NamedComputeProfileRequest(
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            computeAddress.Name,
                            target.Id,
                            spec.Placement.ComputeProfile.Revision
                        ),
                        ct
                    )

                let! storage =
                    resolveStorage.Invoke(
                        NamedStorageProfileRequest(
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            storageAddress.Name,
                            target.Id,
                            spec.Storage.SizeGb,
                            spec.Storage.Profile.Revision
                        ),
                        ct
                    )

                match compute.Value, storage.Value with
                | (:? ResolvedComputeProfile as compute), (:? ResolvedStorageProfile as storage) when
                    matches computeAddress compute.ComputeProfileId
                    && matches storageAddress storage.ProfileId
                    ->
                    let! computeState =
                        loadCompute.Invoke(
                            ComputeProfilePlanningRequest(
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                compute.ComputeProfileId,
                                target.Id,
                                spec.Placement.ComputeProfile.Revision
                            ),
                            session,
                            ct
                        )

                    let! storageState =
                        loadStorage.Invoke(
                            StorageProfilePlanningRequest(
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                storage.ProfileId,
                                target.Id,
                                spec.Storage.SizeGb,
                                spec.Storage.Profile.Revision
                            ),
                            session,
                            ct
                        )

                    match computeState.Value, storageState.Value with
                    | (:? ComputeProfilePlanningSnapshot as compute), (:? StorageProfilePlanningSnapshot as storage) ->
                        let current =
                            request.References
                            |> Seq.filter (fun item ->
                                item.Address <> targetAddress
                                && item.Address <> computeAddress
                                && item.Address <> storageAddress)

                        return
                            ReadPreviewReferencesOutcome(
                                GitPreviewReferencesSnapshot(
                                    [| yield! current
                                       GitPreviewResolvedReference(
                                           targetAddress,
                                           target.Id,
                                           DeclarationVersionToken(
                                               [| DeclarationDocumentVersion(
                                                      DeploymentTargetVersionKeys.Revision(target.Id),
                                                      target.Revision
                                                  )
                                                  DeclarationDocumentVersion(
                                                      DeploymentTargetVersionKeys.WorkloadConfiguration(target.Id),
                                                      target.WorkloadConfiguration.Revision
                                                  ) |]
                                           )
                                       )
                                       GitPreviewResolvedReference(
                                           computeAddress,
                                           compute.Compute.ComputeProfileId,
                                           DeclarationVersionToken(
                                               [| DeclarationDocumentVersion(
                                                      ComputeProfileVersionKeys.Policy(compute.Compute.ComputeProfileId),
                                                      compute.PolicyRevision
                                                  ) |]
                                           )
                                       )
                                       GitPreviewResolvedReference(
                                           storageAddress,
                                           storage.Storage.ProfileId,
                                           DeclarationVersionToken(
                                               [| DeclarationDocumentVersion(
                                                      StorageProfileVersionKeys.Policy(storage.Storage.ProfileId),
                                                      storage.PolicyRevision
                                                  ) |]
                                           )
                                       ) |]
                                )
                            )
                    | _ ->
                        return
                            ReadPreviewReferencesOutcome(
                                PostgresDeclarations.blocked PostgresDeclarationError.StorageProfileUnavailable
                            )
                | _ ->
                    return
                        ReadPreviewReferencesOutcome(
                            PostgresDeclarations.blocked PostgresDeclarationError.ComputeProfileUnavailable
                        )
            | _ ->
                return
                    ReadPreviewReferencesOutcome(
                        PostgresDeclarations.blocked PostgresDeclarationError.DeploymentTargetUnavailable
                    )
        }

    let projection (data: PostgresPlanningDependencies) (current: PostgresCluster) desired (spec: PostgresDeclaration) =
        let placement = data.Placements.Value.Items[0]
        let bootstrap = PostgresPlanning.bootstrap current

        PostgresDeclarations.project
            DeclarationPhase.Configure
            { Identity =
                { ResourceGroupId = current.GroupId
                  IsolationScopeId = placement.IsolationScopeId
                  DeploymentTargetId = placement.DeploymentTargetId
                  Version = current.Spec.Version
                  BootstrapSecretId =
                    if desired then
                        data.BootstrapSecretId
                    else
                        bootstrap |> Option.map _.SourceSecretId |> Option.defaultValue null
                  BootstrapKey =
                    if desired then
                        spec.BootstrapPassword.Key
                    else
                        bootstrap |> Option.map _.SourceKey |> Option.defaultValue null
                  StorageProfileId = current.Spec.StorageProfileId
                  Storage = placement.Storage }
              Instances =
                if desired then
                    PostgresDeclarations.instances spec
                else
                    current.Spec.Instances
              StorageSizeGb =
                if desired then
                    spec.Storage.SizeGb
                else
                    current.Spec.Storage
              FixedReplicas =
                if desired then
                    PostgresDeclarations.instances spec
                else
                    placement.FixedReplicas
              Compute =
                if desired then
                    PostgresResourceDeploymentMappings.compute data.Catalog.Compute.Compute
                else
                    placement.Compute }

    member _.ReadReferencesAsync(request, session, ct) = references request session ct

    member _.PreviewAsync
        (request: PreviewLifecycleAdoptionRequest, session: IClientSessionHandle, ct: CancellationToken)
        =
        task {
            let! resolved =
                references
                    (ReadPreviewReferencesRequest(request.TenantId, request.Declaration, request.References))
                    session
                    ct

            let! read =
                match resolved.Value with
                | :? GitPreviewReferencesSnapshot as refs ->
                    reader.ReadAdoptionAsync(
                        PreviewLifecycleAdoptionRequest(
                            request.TenantId,
                            request.Declaration,
                            request.ResourceId,
                            refs.References
                        ),
                        session,
                        ct
                    )
                | :? DeclarationReadBlocked as blocked -> System.Threading.Tasks.Task.FromResult(Error blocked.Failure)
                | _ -> invalidOp "Unsupported PostgreSQL preview reference outcome."

            match read with
            | Error failure -> return PreviewLifecycleAdoptionOutcome(DeclarationReadBlocked failure)
            | Ok data ->
                match data.Current, data.Placements with
                | Some current, Some placements when placements.Items.Count = 1 ->
                    let spec = DeclarationJson.Read<PostgresDeclaration> request.Declaration.Spec

                    let versions =
                        DeclarationVersionToken(
                            [| DeclarationDocumentVersion(
                                   DeclarationVersionKeys.Resource(PostgresDeclarations.Kind, current.Id),
                                   current.Revision
                               )
                               DeclarationDocumentVersion(
                                   DeclarationVersionKeys.PlanningFence(PostgresDeclarations.Kind, current.Id),
                                   current.PlanningFence
                               )
                               yield! PostgresPlanning.versions data |]
                        )

                    let desired = projection data current true spec

                    return
                        PreviewLifecycleAdoptionOutcome(
                            GitLifecycleAdoptionPreview(
                                CurrentDeclaration(
                                    current.Id,
                                    versions,
                                    projection data current false spec,
                                    if current.IsPrepared then
                                        DeclarationConfigurationState.Prepared
                                    else
                                        DeclarationConfigurationState.Configured
                                ),
                                DeclarationProjection(
                                    desired.Identity,
                                    desired.Configuration,
                                    DeclarationVersionToken(PostgresPlanning.versions data)
                                ),
                                (resolved.Value :?> GitPreviewReferencesSnapshot).References
                            )
                        )
                | _ ->
                    return
                        PreviewLifecycleAdoptionOutcome(
                            PostgresDeclarations.blocked PostgresDeclarationError.PlacementUnavailable
                        )
        }

    member _.AdoptAsync(request: AdoptLifecycleResourceRequest, session: IClientSessionHandle, ct: CancellationToken) =
        task {
            let operation = request.Operation

            let! authorized =
                git.AuthorizeAsync(
                    AuthorizeGitResourceMutationRequest(
                        operation.Context.TenantId,
                        PostgresDeclarations.Kind,
                        operation.ResourceId,
                        Lifecycle = request.Authority,
                        LifecycleEffect = System.Nullable(GitLifecycleMutationEffect.Adopt)
                    ),
                    session,
                    ct
                )

            if not authorized then
                return AdoptLifecycleResourceOutcome(StaleGitExecution())
            else
                let! read = reader.ReadAsync(operation, session, ct)

                match read with
                | Error failure -> return AdoptLifecycleResourceOutcome(DeclarationReadBlocked failure)
                | Ok data ->
                    let! fenced = planner.FenceAsync(FenceDeclarationRequest(operation, request.Expected), session, ct)

                    match fenced.Value with
                    | :? DeclarationReadBlocked as blocked -> return AdoptLifecycleResourceOutcome(blocked)
                    | :? DeclarationPreconditionsChanged as changed -> return AdoptLifecycleResourceOutcome(changed)
                    | :? DeclarationFenced ->
                        let! current =
                            resources
                                .Find(session, PostgresFilters.byId operation.Context.TenantId operation.ResourceId)
                                .SingleAsync(ct)

                        match current.Provisioning, data.Adoption with
                        | (:? DirectPostgresProvisioning), Some credential ->
                            let spec = DeclarationJson.Read<PostgresDeclaration> operation.Declaration.Spec

                            let bootstrap =
                                PostgresBootstrapCredential.Create(
                                    data.BootstrapSecretId,
                                    spec.BootstrapPassword.Key,
                                    credential.SecretId
                                )

                            let next = current.AdoptConfigured(bootstrap)

                            let! replaced =
                                resources.ReplaceOneAsync(
                                    session,
                                    PostgresFilters.atRevision current,
                                    next,
                                    cancellationToken = ct
                                )

                            if replaced.ModifiedCount <> 1L then
                                return AdoptLifecycleResourceOutcome(DeclarationPreconditionsChanged())
                            else
                                do!
                                    outbox.Invoke(
                                        session,
                                        [| OutboxWrite.Create(PostgresClusterEvents.updated current next) |],
                                        ct
                                    )

                                return AdoptLifecycleResourceOutcome(GitLifecycleResourceAdopted())
                        | (:? ConfiguredPostgresProvisioning), _
                        | (:? PreparedPostgresProvisioning), _ ->
                            return AdoptLifecycleResourceOutcome(GitLifecycleResourceAdopted())
                        | _ ->
                            return
                                AdoptLifecycleResourceOutcome(
                                    PostgresDeclarations.blocked PostgresDeclarationError.ExplicitAdoptionRequired
                                )
                    | _ -> return invalidOp "Unsupported PostgreSQL adoption fence outcome."
        }
