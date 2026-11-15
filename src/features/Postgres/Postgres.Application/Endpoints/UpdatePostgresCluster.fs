namespace Pole.Features.Postgres.Application.Endpoints

open System
open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.Validation

type UpdatePostgresClusterRequestBody =
    { ExpectedRevision: int64
      Instances: int option
      Storage: int option
      ComputeProfileId: string option
      ReplicationMode: PostgresReplicationMode option }

type UpdatePostgresClusterResponse =
    { Id: string
      Placement: PlacementSummaryDto }

type UpdatePostgresClusterOutcome =
    | PostgresClusterUpdated of UpdatePostgresClusterResponse
    | UpdatePostgresClusterRejected of UpdatePostgresClusterError

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId: string
      [<FromBody>]
      Body: UpdatePostgresClusterRequestBody
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId: string }

    interface IMediatorRequest<UpdatePostgresClusterOutcome>

    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type private UpdatePostgresClusterPlan =
    { Cluster: PostgresCluster
      CurrentCompute: CurrentResourceDeploymentComputeDto
      UpdatedCluster: PostgresCluster
      ReplacementDeploymentCompute: ResourceDeploymentComputeConfiguration option
      CurrentQuotaInput: PostgresQuotaInput
      NextQuotaInput: PostgresQuotaInput }

type private UpdatePostgresClusterPreparation =
    | Completed of UpdatePostgresClusterOutcome
    | ReadyToUpdate of UpdatePostgresClusterPlan

[<RequireQualifiedAccess>]
module private UpdatePostgresClusterWorkflow =
    let private resolveReplacementCompute
        (resolveComputeProfile: ResolveComputeProfile)
        (cluster: PostgresCluster)
        (currentCompute: CurrentResourceDeploymentComputeDto)
        (computeProfileId: string option)
        cancellationToken
        : Threading.Tasks.Task<Result<ResolvedComputeProfile option, UpdatePostgresClusterError>> =
        task {
            match computeProfileId with
            | Some computeProfileId when not (String.IsNullOrWhiteSpace computeProfileId) ->
                let! computeResult =
                    resolveComputeProfile.Invoke(
                        ComputeProfileResolutionRequest(
                            computeProfileId,
                            currentCompute.DeploymentTargetId,
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType
                        ),
                        cancellationToken
                    )

                match TypedResultInterop.computeProfile computeResult with
                | Error error -> return Error(UpdateCompute error)
                | Ok compute when not (String.IsNullOrWhiteSpace compute.RuntimeClassName) ->
                    return
                        Error(
                            UpdateState(
                                UpdatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported cluster.Id
                            )
                        )
                | Ok compute -> return Ok(Some compute)
            | _ -> return Ok None
        }

    let prepare
        (loadCluster: LoadPostgresClusterById)
        (resolveStorageProfile: ResolveStorageProfile)
        (resolveComputeProfile: ResolveComputeProfile)
        (loadDeploymentCompute: LoadCurrentResourceDeploymentCompute)
        (request: UpdatePostgresClusterRequest)
        cancellationToken
        =
        task {
            let! cluster = loadCluster.Invoke(request.TenantId, request.ClusterId, cancellationToken)

            match cluster with
            | None ->
                return
                    Completed(
                        UpdatePostgresClusterRejected(UpdateState(PostgresClusterUpdateNotFound request.ClusterId))
                    )
            | Some cluster when request.Body.ExpectedRevision <> cluster.Revision ->
                return
                    Completed(
                        UpdatePostgresClusterRejected(
                            UpdateConflict
                                { ClusterId = cluster.Id
                                  Reason = PostgresMutationConflictReason.ExpectedRevisionConflict }
                        )
                    )
            | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) || cluster.IsPrepared ->
                return
                    Completed(UpdatePostgresClusterRejected(UpdateState(PostgresClusterUpdateUnavailable cluster.Id)))
            | Some cluster when not (PostgresReplication.valid
                (defaultArg request.Body.Instances cluster.Spec.Instances)
                (defaultArg request.Body.ReplicationMode cluster.Spec.ReplicationMode)) ->
                return Completed(UpdatePostgresClusterRejected(UpdateState(PostgresClusterSynchronousHaRequired cluster.Id)))
            | Some cluster ->
                let updatedInstances = defaultArg request.Body.Instances cluster.Spec.Instances
                let updatedStorage = defaultArg request.Body.Storage cluster.Spec.Storage

                let! currentComputeResult =
                    loadDeploymentCompute.Invoke(
                        LoadCurrentResourceDeploymentComputeRequest(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id
                        ),
                        cancellationToken
                    )

                match TypedResultInterop.currentResourceDeploymentCompute currentComputeResult with
                | Error error -> return Completed(UpdatePostgresClusterRejected(UpdateCurrentCompute error))
                | Ok currentCompute ->
                    let! replacementComputeResult =
                        resolveReplacementCompute
                            resolveComputeProfile
                            cluster
                            currentCompute
                            request.Body.ComputeProfileId
                            cancellationToken

                    match replacementComputeResult with
                    | Error error -> return Completed(UpdatePostgresClusterRejected error)
                    | Ok replacementCompute ->
                        let! storageProfileResult =
                            resolveStorageProfile.Invoke(
                                StorageProfileResolutionRequest(
                                    cluster.Spec.StorageProfileId,
                                    currentCompute.DeploymentTargetId,
                                    cluster.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    updatedStorage
                                ),
                                cancellationToken
                            )

                        match TypedResultInterop.storageProfile storageProfileResult with
                        | Error error -> return Completed(UpdatePostgresClusterRejected(UpdateStorage error))
                        | Ok _ ->
                            let updatedCluster = cluster.UpdateDesiredState(
                                updatedInstances, updatedStorage,
                                defaultArg request.Body.ReplicationMode cluster.Spec.ReplicationMode)

                            let currentQuotaInput =
                                PostgresQuotaInput.create
                                    currentCompute.Compute.Requests.CpuMillis
                                    currentCompute.Compute.Requests.MemoryMi
                                    (int64 cluster.Spec.Instances)

                            let (nextRequestsCpuMillis, nextRequestsMemoryMi, replacementDeploymentCompute) =
                                match replacementCompute with
                                | Some compute ->
                                    compute.Requests.CpuMillis,
                                    compute.Requests.MemoryMi,
                                    Some(PostgresResourceDeploymentMappings.compute compute)
                                | None ->
                                    currentCompute.Compute.Requests.CpuMillis,
                                    currentCompute.Compute.Requests.MemoryMi,
                                    None

                            let nextQuotaInput =
                                PostgresQuotaInput.create
                                    nextRequestsCpuMillis
                                    nextRequestsMemoryMi
                                    (int64 updatedCluster.Spec.Instances)

                            return
                                ReadyToUpdate(
                                    { Cluster = cluster
                                      CurrentCompute = currentCompute
                                      UpdatedCluster = updatedCluster
                                      ReplacementDeploymentCompute = replacementDeploymentCompute
                                      CurrentQuotaInput = currentQuotaInput
                                      NextQuotaInput = nextQuotaInput }
                                )
        }

    let private applyPlacement
        (loadPlanning: LoadResourceDeploymentPlanningSnapshot)
        (apply: RequestExactResourceDeploymentApply)
        (plan: UpdatePostgresClusterPlan)
        session ct =
        task {
            let! loaded = loadPlanning.Invoke(
                ResourceDeploymentPlanningRequest(plan.Cluster.TenantId, PostgresResourceDeployment.ResourceType, plan.Cluster.Id),
                session, ct)
            match loaded.Value with
            | :? ResourceDeploymentPlanningSnapshot as snapshot when snapshot.Items.Count = 1 ->
                let placement = snapshot.Items[0]
                if placement.State = ResourceDeploymentPlanningState.Deleting then
                    return ResourceDeploymentMutationOutcome(ResourceDeploymentMutationError(ResourceDeploymentPendingDeletion(plan.Cluster.Id)))
                else
                    return! apply.Invoke(ResourceDeploymentExactMutationRequest(
                        plan.Cluster.TenantId, PostgresResourceDeployment.ResourceType, plan.Cluster.Id,
                        placement.DeploymentId, placement.Revision, plan.CurrentCompute.Compute.Profile,
                        plan.ReplacementDeploymentCompute |> Option.toObj,
                        FixedReplicas = Nullable plan.UpdatedCluster.Spec.Instances,
                        ResourceRevision = Nullable plan.UpdatedCluster.Revision), session, ct)
            | :? ResourceDeploymentPlanningSnapshot ->
                return ResourceDeploymentMutationOutcome(ResourceDeploymentMutationError(ResourceDeploymentCardinalityConflict(plan.Cluster.Id)))
            | :? ResourceDeploymentPlanningUnavailable ->
                return ResourceDeploymentMutationOutcome(ResourceDeploymentMutationError(ResourceDeploymentMutationNotFound(plan.Cluster.Id)))
            | _ -> return invalidOp "Unknown PostgreSQL placement planning outcome."
        }

    let execute
        (quotaAdmissionExecutor: IQuotaAdmissionExecutor)
        (persistUpdatedCluster: PersistUpdatedPostgresCluster)
        (requestDeploymentApply: RequestExactResourceDeploymentApply)
        (loadPlanning: LoadResourceDeploymentPlanningSnapshot)
        (plan: UpdatePostgresClusterPlan)
        cancellationToken
        =
        quotaAdmissionExecutor.ExecuteOutcomeAsync<PostgresQuotaInput, UpdatePostgresClusterOutcome>(
            ExecuteQuotaAdmissionRequest<PostgresQuotaInput, UpdatePostgresClusterOutcome>(
                plan.Cluster.TenantId,
                PostgresQuotaResourceKey.create plan.Cluster.Id,
                UpdateQuotaMutation<PostgresQuotaInput>(plan.CurrentQuotaInput, plan.NextQuotaInput),
                (fun error -> UpdatePostgresClusterRejected(UpdateQuota error)),
                (fun transaction ct ->
                    task {
                        let! persisted =
                            persistUpdatedCluster.Invoke(
                                { Current = plan.Cluster
                                  Replacement = plan.UpdatedCluster
                                  Mutation = None },
                                transaction.Session,
                                ct
                            )

                        match persisted with
                        | PostgresError error ->
                            return transaction.Abort(UpdatePostgresClusterRejected(UpdateConflict error))
                        | PostgresSuccess() ->
                            let! deploymentResult =
                                applyPlacement loadPlanning requestDeploymentApply plan transaction.Session ct

                            match TypedResultInterop.resourceDeploymentMutation deploymentResult with
                            | Error error ->
                                return transaction.Abort(UpdatePostgresClusterRejected(UpdateDeployment error))
                            | Ok deployment ->
                                return
                                    transaction.Commit(
                                        PostgresClusterUpdated(
                                            { Id = plan.Cluster.Id
                                              Placement = deployment }
                                        )
                                    )
                    })
            ),
            cancellationToken
        )

type UpdatePostgresClusterHandler
    (
        loadCluster: LoadPostgresClusterById,
        persistUpdatedCluster: PersistUpdatedPostgresCluster,
        resolveStorageProfile: ResolveStorageProfile,
        quotaAdmissionExecutor: IQuotaAdmissionExecutor,
        resolveComputeProfile: ResolveComputeProfile,
        loadDeploymentCompute: LoadCurrentResourceDeploymentCompute,
        requestDeploymentApply: RequestExactResourceDeploymentApply,
        loadPlanning: LoadResourceDeploymentPlanningSnapshot
    ) =
    interface IMediatorOutcomeHandler<UpdatePostgresClusterRequest, UpdatePostgresClusterOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! preparation =
                    UpdatePostgresClusterWorkflow.prepare
                        loadCluster
                        resolveStorageProfile
                        resolveComputeProfile
                        loadDeploymentCompute
                        request
                        cancellationToken

                match preparation with
                | Completed outcome -> return outcome
                | ReadyToUpdate plan ->
                    return!
                        UpdatePostgresClusterWorkflow.execute
                            quotaAdmissionExecutor
                            persistUpdatedCluster
                            requestDeploymentApply
                            loadPlanning
                            plan
                            cancellationToken
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()

    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore

        this
            .RuleFor(_.Body.ExpectedRevision)
            .GreaterThan(0L)
            .WithMessage("Expected revision must be greater than zero.")
        |> ignore

        this
            .RuleFor(_.Body.Instances)
            .Must(fun instances -> Option.forall (fun value -> value > 0) instances)
            .WithMessage("Instances must be greater than zero.")
        |> ignore

        this
            .RuleFor(_.Body.Storage)
            .Must(fun storage -> Option.forall (fun value -> value > 0) storage)
            .WithMessage("Storage must be greater than zero.")
        |> ignore

        this
            .RuleFor(fun request -> Option.toObj request.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID")
            .OverridePropertyName("Body.ComputeProfileId")
        |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresCluster =
    [<Extension>]
    static member MapUpdatePostgresClusterEndpoint(endpoints: IEndpointRouteBuilder) =
        endpoints
            .MapMethod<UpdatePostgresCluster>(
                "/{ClusterId}",
                nameof UpdatePostgresCluster.Handle,
                EndpointHttpMethod.Put
            )
            .WithName("UpdatePostgresCluster")
            .Produces<UpdatePostgresClusterResponse>(StatusCodes.Status202Accepted)
        |> ignore

        endpoints

    static member Handle
        (
            [<AsParameters>] request: UpdatePostgresClusterRequest,
            [<FromServices>] mediator: IMediator,
            cancellationToken: System.Threading.CancellationToken
        ) =
        task {
            let! result = mediator.Send(request, cancellationToken)

            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterUpdated value -> Results.Accepted(value = value)
                    | UpdatePostgresClusterRejected error -> (PostgresHttpMappings.mapUpdateError error).ToHttpResult())
        }
