namespace Pole.Features.Postgres.Application.Endpoints

open System
open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.ResultType
open Pole.Validation

type UpdatePostgresClusterRequestBody =
    { Instances : int option
      Storage : int option
      ComputeProfileId : string option }

type UpdatePostgresClusterResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterStateError =
    | PostgresClusterUpdateNotFound of clusterId : string
    | PostgresClusterUpdateUnavailable of clusterId : string
    | PostgresClusterStatefulSetInstancesUnsupported of clusterId : string
    | PostgresClusterComputeRuntimeClassUnsupported of clusterId : string

type UpdatePostgresClusterError =
    | UpdateState of UpdatePostgresClusterStateError
    | UpdateCompute of ComputeProfileResolutionError
    | UpdateCurrentCompute of LoadCurrentResourceDeploymentComputeError
    | UpdateStorage of StorageProfileResolutionError
    | UpdateDeployment of ResourceDeploymentMutationError
    | UpdateQuota of QuotaExecutionError
    | UpdateConflict of PostgresClusterUpdateConflict
    interface IUnion with
        member this.Value =
            match this with
            | UpdateState error -> box error
            | UpdateCompute error -> box error
            | UpdateCurrentCompute error -> box error
            | UpdateStorage error -> box error
            | UpdateDeployment error -> box error
            | UpdateQuota error -> box error
            | UpdateConflict error -> box error

type UpdatePostgresClusterOutcome =
    | PostgresClusterUpdated of UpdatePostgresClusterResponse
    | UpdatePostgresClusterRejected of UpdatePostgresClusterError

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

[<Struct>]
type private PostgresComputeAdoption =
    | Preserve
    | Replace of ResolvedComputeProfile

type UpdatePostgresClusterHandler(
    loadCluster : LoadPostgresClusterById,
    persistUpdatedCluster : PersistUpdatedPostgresCluster,
    resolveStorageProfile : ResolveStorageProfile,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    resolveComputeProfile : ResolveComputeProfile,
    loadDeploymentCompute : LoadCurrentResourceDeploymentCompute,
    requestDeploymentApply : RequestResourceDeploymentApply) =
    interface IMediatorOutcomeHandler<UpdatePostgresClusterRequest, UpdatePostgresClusterOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! cluster =
                    loadCluster.Invoke(
                        request.TenantId,
                        request.ClusterId,
                        cancellationToken)

                match cluster with
                | None ->
                    return
                        UpdatePostgresClusterRejected(
                            UpdateState(PostgresClusterUpdateNotFound request.ClusterId))
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        UpdatePostgresClusterRejected(
                            UpdateState(PostgresClusterUpdateUnavailable cluster.Id))
                | Some cluster
                    when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet &&
                         defaultArg request.Body.Instances cluster.Spec.Instances <> 1 ->
                    return
                        UpdatePostgresClusterRejected(
                            UpdateState(
                                PostgresClusterStatefulSetInstancesUnsupported cluster.Id))
                | Some cluster ->
                    let updatedInstances =
                        defaultArg request.Body.Instances cluster.Spec.Instances
                    let updatedStorage =
                        defaultArg request.Body.Storage cluster.Spec.Storage

                    let! currentComputeResult =
                        loadDeploymentCompute.Invoke(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id,
                            cancellationToken)

                    let mutable currentCompute = Unchecked.defaultof<CurrentResourceDeploymentComputeDto>
                    let mutable currentComputeError =
                        Unchecked.defaultof<LoadCurrentResourceDeploymentComputeError>
                    if not (TypedResultExtensions.TryGetValue(
                        currentComputeResult,
                        &currentCompute,
                        &currentComputeError)) then
                        return UpdatePostgresClusterRejected(UpdateCurrentCompute currentComputeError)
                    else
                        let! replacementComputeResult =
                            task {
                                match request.Body.ComputeProfileId with
                                | Some computeProfileId when not (String.IsNullOrWhiteSpace computeProfileId) ->
                                    let! computeResult =
                                        resolveComputeProfile.Invoke(
                                            ComputeProfileResolutionRequest(
                                                computeProfileId,
                                                currentCompute.DeploymentTargetId,
                                                cluster.TenantId,
                                                PostgresResourceDeployment.ResourceType),
                                            cancellationToken)
                                    let mutable compute = Unchecked.defaultof<ResolvedComputeProfile>
                                    let mutable computeError =
                                        Unchecked.defaultof<ComputeProfileResolutionError>
                                    if not (TypedResultExtensions.TryGetValue(
                                        computeResult,
                                        &compute,
                                        &computeError)) then
                                        return PostgresError(UpdateCompute computeError)
                                    elif cluster.Spec.DeploymentMode = PostgresDeploymentMode.Operator &&
                                         not (String.IsNullOrWhiteSpace compute.RuntimeClassName) then
                                        return
                                            PostgresError(
                                                UpdateState(
                                                    PostgresClusterComputeRuntimeClassUnsupported cluster.Id))
                                    else
                                        return PostgresSuccess(Replace compute)
                                | _ -> return PostgresSuccess Preserve
                            }

                        match replacementComputeResult with
                        | PostgresError error -> return UpdatePostgresClusterRejected error
                        | PostgresSuccess replacementCompute ->
                            let! storageProfileResult =
                                resolveStorageProfile.Invoke(
                                    StorageProfileResolutionRequest(
                                        cluster.Spec.StorageProfileId,
                                        currentCompute.DeploymentTargetId,
                                        cluster.TenantId,
                                        ResourceType.Postgres,
                                        updatedStorage),
                                    cancellationToken)
                            let mutable ignoredStorage = Unchecked.defaultof<ResolvedStorageProfile>
                            let mutable storageError =
                                Unchecked.defaultof<StorageProfileResolutionError>
                            if not (TypedResultExtensions.TryGetValue(
                                storageProfileResult,
                                &ignoredStorage,
                                &storageError)) then
                                return UpdatePostgresClusterRejected(UpdateStorage storageError)
                            else
                                let updatedCluster =
                                    cluster.UpdateDesiredState(updatedInstances, updatedStorage)
                                let currentQuotaInput =
                                    PostgresQuotaInput.create
                                        currentCompute.Compute.Requests.CpuMillis
                                        currentCompute.Compute.Requests.MemoryMi
                                        (int64 cluster.Spec.Instances)
                                let nextRequestsCpuMillis, nextRequestsMemoryMi =
                                    match replacementCompute with
                                    | Replace compute -> compute.Requests.CpuMillis, compute.Requests.MemoryMi
                                    | Preserve ->
                                        currentCompute.Compute.Requests.CpuMillis,
                                        currentCompute.Compute.Requests.MemoryMi
                                let replacementDeploymentCompute =
                                    match replacementCompute with
                                    | Replace compute ->
                                        PostgresResourceDeploymentMappings.compute compute
                                    | Preserve -> null
                                let nextQuotaInput =
                                    PostgresQuotaInput.create
                                        nextRequestsCpuMillis
                                        nextRequestsMemoryMi
                                        (int64 updatedCluster.Spec.Instances)

                                return!
                                    quotaAdmissionExecutor.ExecuteOutcomeAsync<
                                        PostgresQuotaInput,
                                        UpdatePostgresClusterOutcome>(
                                        cluster.TenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        UpdateQuotaMutation<PostgresQuotaInput>(
                                            currentQuotaInput,
                                            nextQuotaInput),
                                        Func<QuotaExecutionError, UpdatePostgresClusterOutcome>(
                                            fun error ->
                                                UpdatePostgresClusterRejected(UpdateQuota error)),
                                        Func<
                                            MongoTransactionContext<UpdatePostgresClusterOutcome>,
                                            Threading.CancellationToken,
                                            Threading.Tasks.Task<MongoTransactionDecision<UpdatePostgresClusterOutcome>>>(
                                            fun transaction ct ->
                                                task {
                                                    let! persisted =
                                                        persistUpdatedCluster.Invoke(
                                                            transaction.Session,
                                                            cluster,
                                                            updatedCluster,
                                                            ct)
                                                    match persisted with
                                                    | PostgresError error ->
                                                        return
                                                            transaction.Abort(
                                                                UpdatePostgresClusterRejected(
                                                                    UpdateConflict error))
                                                    | PostgresSuccess () ->
                                                        let! deploymentResult =
                                                            requestDeploymentApply.Invoke(
                                                                ResourceDeploymentMutationRequest(
                                                                    cluster.TenantId,
                                                                    PostgresResourceDeployment.ResourceType,
                                                                    cluster.Id,
                                                                    currentCompute.Compute.Profile,
                                                                    replacementDeploymentCompute),
                                                                transaction.Session,
                                                                ct)
                                                        let mutable deployment =
                                                            Unchecked.defaultof<PlacementSummaryDto>
                                                        let mutable deploymentError =
                                                            Unchecked.defaultof<ResourceDeploymentMutationError>
                                                        if not (TypedResultExtensions.TryGetValue(
                                                            deploymentResult,
                                                            &deployment,
                                                            &deploymentError)) then
                                                            return
                                                                transaction.Abort(
                                                                    UpdatePostgresClusterRejected(
                                                                        UpdateDeployment deploymentError))
                                                        else
                                                            return
                                                                transaction.Commit(
                                                                    PostgresClusterUpdated(
                                                                        { Id = cluster.Id
                                                                          Placement = deployment }))
                                                }),
                                        cancellationToken)
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore
        this.RuleFor(_.Body.Instances)
            .Must(fun instances -> Option.forall (fun value -> value > 0) instances)
            .WithMessage("Instances must be greater than zero.")
        |> ignore
        this.RuleFor(_.Body.Storage)
            .Must(fun storage -> Option.forall (fun value -> value > 0) storage)
            .WithMessage("Storage must be greater than zero.")
        |> ignore
        this.RuleFor(fun request -> Option.toObj request.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID")
            .OverridePropertyName("Body.ComputeProfileId")
        |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresCluster =
    [<Extension>]
    static member MapUpdatePostgresClusterEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<UpdatePostgresCluster>(
            "/{ClusterId}",
            nameof UpdatePostgresCluster.Handle,
            EndpointHttpMethod.Put)
            .WithName("UpdatePostgresCluster")
            .Produces<UpdatePostgresClusterResponse>(StatusCodes.Status202Accepted)
        |> ignore
        endpoints

    static member Handle(
        [<AsParameters>] request : UpdatePostgresClusterRequest,
        [<FromServices>] mediator : IMediator,
        [<FromServices>] httpProblems : IHttpProblemMapper,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterUpdated value -> Results.Accepted(value = value)
                    | UpdatePostgresClusterRejected error ->
                        httpProblems.Map(error).ToHttpResult())
        }
