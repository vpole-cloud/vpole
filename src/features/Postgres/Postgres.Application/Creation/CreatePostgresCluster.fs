namespace Pole.Features.Postgres.Application.Creation

open System
open MongoDB.Bson
open Pole.Features.Backups.Contracts
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Credentials
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.StorageProfiles.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Workloads

type PostgresCreationConfiguration =
    { Name: string
      GroupId: string
      Version: int
      Instances: int option
      Storage: int option
      StorageProfileId: string
      ComputeProfileId: string option
      Placement: PlacementRequest option }

type PostgresRestorePlacement = { RuntimeCellId: string }

type PostgresCreationAuthority =
    | NewCluster of administratorPassword: string
    | Recovery of
        origin: PostgresRecoveryOrigin *
        placement: PostgresRestorePlacement *
        recordTarget: RecordRestoreTargetCreation

    override _.ToString() = "PostgresCreationAuthority([REDACTED])"

[<RequireQualifiedAccess>]
module private PostgresCreationAuthority =
    let acceptsPlacement authority runtimeCellId =
        match authority with
        | PostgresCreationAuthority.NewCluster _ -> true
        | PostgresCreationAuthority.Recovery(_, placement, _) -> placement.RuntimeCellId = runtimeCellId

type PostgresCreationCommand =
    { TenantId: string
      ResourceId: string
      Configuration: PostgresCreationConfiguration
      Authority: PostgresCreationAuthority }

    override _.ToString() = "PostgresCreationCommand([REDACTED])"

type PostgresCreationResult =
    { Cluster: PostgresCluster
      Deployment: PlacementSummaryDto }

type PostgresCreationOutcome =
    | Created of PostgresCreationResult
    | Rejected of CreatePostgresClusterError

type private CreatePostgresClusterPlan =
    { Cluster: PostgresCluster
      Credential: PreparedManagedSecret
      DeploymentId: string
      IsolationScopeId: string option
      Target: ResolvedResourceDeploymentTarget
      Compute: ResourceDeploymentComputeConfiguration
      Storage: ResourceDeploymentStorageConfiguration
      QuotaInput: PostgresQuotaInput }

type private CreatePostgresClusterPreparation =
    | Completed of PostgresCreationOutcome
    | ReadyToCreate of CreatePostgresClusterPlan

[<RequireQualifiedAccess>]
module private CreatePostgresClusterWorkflow =
    let private placementIds (request: PostgresCreationCommand) =
        let value selector =
            request.Configuration.Placement
            |> Option.bind (fun placement ->
                let value = selector placement
                if String.IsNullOrWhiteSpace value then None else Some value)

        value (fun placement -> placement.IsolationScopeId), value (fun placement -> placement.DeploymentTargetId)

    let private resolvedTarget (target: DeploymentTargetLookupDto) =
        let ingress =
            target.WorkloadConfiguration.Ingress
            |> Option.ofObj
            |> Option.map (fun value ->
                ResourceDeploymentIngressConfiguration.Create(
                    value.IngressClassName,
                    value.ClusterIssuerName,
                    value.PublicBaseDomain,
                    null
                ))

        ResolvedResourceDeploymentTarget(target.Id, target.WorkloadConfiguration.Revision, ingress |> Option.toObj)

    let prepare
        (admitDeploymentTarget: AdmitDeploymentTargetForNewWorkload)
        (resolveComputeProfile: ResolveComputeProfile)
        (resolveStorageProfile: ResolveStorageProfile)
        (createCredential: CreatePostgresCredential)
        (request: PostgresCreationCommand)
        cancellationToken
        =
        task {
            let isolationScopeId, explicitDeploymentTargetId = placementIds request
            let bootstrap, password =
                match request.Authority with
                | PostgresCreationAuthority.NewCluster password -> FreshPostgresBootstrap() :> PostgresClusterBootstrap, password
                | PostgresCreationAuthority.Recovery(origin, _, _) ->
                    RecoveryPostgresBootstrap(origin) :> PostgresClusterBootstrap,
                    Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))

            let! targetResult =
                admitDeploymentTarget.Invoke(
                    DeploymentTargetAdmissionRequest(
                        request.TenantId,
                        PostgresResourceDeployment.ResourceType,
                        isolationScopeId |> Option.toObj,
                        explicitDeploymentTargetId |> Option.toObj,
                        DeploymentTargetRequiredCapabilities.None
                    ),
                    cancellationToken
                )

            match TypedResultInterop.targetAdmission targetResult with
            | Error error -> return Completed(PostgresCreationOutcome.Rejected(CreateTarget error))
            | Ok target ->
                let! computeProfileResult =
                    resolveComputeProfile.Invoke(
                        ComputeProfileResolutionRequest(
                            request.Configuration.ComputeProfileId |> Option.toObj,
                            target.Id,
                            request.TenantId,
                            PostgresResourceDeployment.ResourceType
                        ),
                        cancellationToken
                    )

                match TypedResultInterop.computeProfile computeProfileResult with
                | Error error -> return Completed(PostgresCreationOutcome.Rejected(CreateCompute error))
                | Ok computeProfile when not (String.IsNullOrWhiteSpace computeProfile.RuntimeClassName) ->
                    return
                        Completed(
                            PostgresCreationOutcome.Rejected(
                                CreateState(
                                    CreatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported
                                        target.Id
                                )
                            )
                        )
                | Ok computeProfile ->
                    let requestedStorage =
                        defaultArg request.Configuration.Storage PostgresCluster.DefaultStorage

                    let! storageProfileResult =
                        resolveStorageProfile.Invoke(
                            StorageProfileResolutionRequest(
                                request.Configuration.StorageProfileId,
                                target.Id,
                                request.TenantId,
                                PostgresResourceDeployment.ResourceType,
                                requestedStorage
                            ),
                            cancellationToken
                        )

                    match TypedResultInterop.storageProfile storageProfileResult with
                    | Error error -> return Completed(PostgresCreationOutcome.Rejected(CreateStorage error))
                    | Ok storageProfile ->
                        let cluster =
                            PostgresCluster.Create(
                                request.TenantId,
                                request.Configuration.Name,
                                request.Configuration.GroupId,
                                request.Configuration.Version,
                                request.Configuration.StorageProfileId,
                                { Id = Some request.ResourceId
                                  Instances = defaultArg request.Configuration.Instances PostgresCluster.DefaultInstances
                                  Storage = requestedStorage }
                            )

                        let cluster = { cluster with Spec = { cluster.Spec with Bootstrap = bootstrap } }

                        let! credentialResult =
                            createCredential.Invoke(
                                cluster.TenantId,
                                cluster.Id,
                                password,
                                cancellationToken
                            )

                        match credentialResult with
                        | PostgresError error -> return Completed(PostgresCreationOutcome.Rejected(CreateCredential error))
                        | PostgresSuccess credential ->
                            let quotaInput =
                                PostgresQuotaInput.create
                                    computeProfile.Requests.CpuMillis
                                    computeProfile.Requests.MemoryMi
                                    (int64 cluster.Spec.Instances)

                            return
                                ReadyToCreate(
                                    { Cluster = cluster
                                      Credential = credential
                                      DeploymentId = ObjectId.GenerateNewId().ToString()
                                      IsolationScopeId = isolationScopeId
                                      Target = resolvedTarget target
                                      Compute = PostgresResourceDeploymentMappings.compute computeProfile
                                      Storage = PostgresResourceDeploymentMappings.storage storageProfile
                                      QuotaInput = quotaInput }
                                )
        }

    let execute
        (quotaAdmissionExecutor: IQuotaAdmissionExecutor)
        (persistCreatedCluster: PersistCreatedPostgresCluster)
        (resourceRegistrar: IResourceRegistrar)
        (createResourceDeployment: CreateResourceDeployment)
        (createBoundSecret: CreateBoundManagedSecret)
        (authority: PostgresCreationAuthority)
        (plan: CreatePostgresClusterPlan)
        cancellationToken
        =
        quotaAdmissionExecutor.ExecuteOutcomeAsync<PostgresQuotaInput, PostgresCreationOutcome>(
            ExecuteQuotaAdmissionRequest<PostgresQuotaInput, PostgresCreationOutcome>(
                plan.Cluster.TenantId,
                PostgresQuotaResourceKey.create plan.Cluster.Id,
                CreateQuotaMutation<PostgresQuotaInput>(plan.QuotaInput),
                (fun error -> PostgresCreationOutcome.Rejected(CreateQuota error)),
                (fun transaction ct ->
                    task {
                        let! persisted = persistCreatedCluster.Invoke(transaction.Session, plan.Cluster, ct)

                        match persisted with
                        | PostgresError error ->
                            return transaction.Abort(PostgresCreationOutcome.Rejected(CreateNameAlreadyExists error))
                        | PostgresSuccess() ->
                            let! registrationResult =
                                resourceRegistrar.RegisterAsync(
                                    RegisterResourceRequest(
                                        plan.Cluster.TenantId,
                                        plan.Cluster.GroupId,
                                        ResourceRegistrationDescriptor(
                                            PostgresResourceDeployment.ResourceType,
                                            plan.Cluster.Id,
                                            plan.Cluster.Name
                                        )
                                    ),
                                    transaction.Session,
                                    ct
                                )

                            match TypedResultInterop.resourceRegistration registrationResult with
                            | Error error ->
                                return transaction.Abort(PostgresCreationOutcome.Rejected(CreateRegistration error))
                            | Ok _ ->
                                let! deploymentResult =
                                    createResourceDeployment.Invoke(
                                        CreateResourceDeploymentRequest(
                                            plan.DeploymentId,
                                            plan.Cluster.TenantId,
                                            PostgresResourceDeployment.ResourceType,
                                            plan.Cluster.Id,
                                            plan.Cluster.GroupId,
                                            plan.IsolationScopeId |> Option.toObj,
                                            plan.Target,
                                            plan.Compute,
                                            plan.Storage,
                                            ResourcePlacementCapability.SingleTarget
                                        ),
                                        transaction.Session,
                                        ct
                                    )

                                match TypedResultInterop.createResourceDeployment deploymentResult with
                                | Error error ->
                                    return transaction.Abort(PostgresCreationOutcome.Rejected(CreateDeployment error))
                                | Ok deployment when not (PostgresCreationAuthority.acceptsPlacement authority deployment.RuntimeCellId) ->
                                    return transaction.Abort(
                                        PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestorePlacementChanged))
                                | Ok deployment ->
                                    let! secretResult =
                                        createBoundSecret.Invoke(
                                            CreateBoundManagedSecretRequest(
                                                plan.Credential,
                                                plan.Cluster.GroupId,
                                                deployment.IsolationScopeId,
                                                deployment.DeploymentId
                                            ),
                                            transaction.Session,
                                            ct
                                        )

                                    match secretResult.Value with
                                    | :? ManagedSecretFailure as error ->
                                        return
                                            transaction.Abort(
                                                PostgresCreationOutcome.Rejected(
                                                    CreateCredential(
                                                        PostgresCredentialErrors.fromManaged plan.Cluster.Id error
                                                    )
                                                )
                                            )
                                    | :? ManagedSecretMutationSucceeded ->
                                        let! creation =
                                            match authority with
                                            | PostgresCreationAuthority.NewCluster _ ->
                                                System.Threading.Tasks.Task.FromResult(RestoreTargetCreationDecision.Recorded)
                                            | PostgresCreationAuthority.Recovery(origin, _, record) ->
                                                record.Invoke(
                                                    RecordRestoreTargetCreationRequest(
                                                        plan.Cluster.TenantId,
                                                        origin.OperationId,
                                                        origin.ArchiveId,
                                                        plan.Cluster.Id
                                                    ),
                                                    transaction.Session,
                                                    ct
                                                )

                                        match creation with
                                        | RestoreTargetCreationDecision.Recorded ->
                                            return transaction.Commit(
                                                PostgresCreationOutcome.Created
                                                    { Cluster = plan.Cluster
                                                      Deployment = deployment })
                                        | RestoreTargetCreationDecision.AlreadyRecorded ->
                                            return transaction.Abort(
                                                PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreTargetAlreadyCreated))
                                        | RestoreTargetCreationDecision.OperationUnavailable ->
                                            return transaction.Abort(
                                                PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreOperationUnavailable))
                                        | _ -> return invalidOp "Unexpected restore target creation decision."
                                    | _ -> return invalidOp "Unexpected managed Secret creation outcome."
                    })
            ),
            cancellationToken
        )

type PostgresClusterCreator
    (
        persistCreatedCluster: PersistCreatedPostgresCluster,
        resourceRegistrar: IResourceRegistrar,
        resolveStorageProfile: ResolveStorageProfile,
        createCredential: CreatePostgresCredential,
        quotaAdmissionExecutor: IQuotaAdmissionExecutor,
        admitDeploymentTarget: AdmitDeploymentTargetForNewWorkload,
        resolveComputeProfile: ResolveComputeProfile,
        createResourceDeployment: CreateResourceDeployment,
        createBoundSecret: CreateBoundManagedSecret
    ) =

    member _.CreateAsync(request: PostgresCreationCommand, cancellationToken) =
        task {
            let! preparation =
                CreatePostgresClusterWorkflow.prepare
                    admitDeploymentTarget
                    resolveComputeProfile
                    resolveStorageProfile
                    createCredential
                    request
                    cancellationToken

            match preparation with
            | Completed outcome -> return outcome
            | ReadyToCreate plan ->
                return!
                    CreatePostgresClusterWorkflow.execute
                        quotaAdmissionExecutor
                        persistCreatedCluster
                        resourceRegistrar
                        createResourceDeployment
                        createBoundSecret
                        request.Authority
                        plan
                        cancellationToken
        }

