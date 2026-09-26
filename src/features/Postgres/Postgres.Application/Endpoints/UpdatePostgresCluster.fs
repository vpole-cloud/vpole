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
open Pole.Validation

type UpdatePostgresClusterRequestBody =
    { Instances : int option
      Storage : int option
      ComputeProfileId : string option }

type UpdatePostgresClusterResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterOutcome =
    | PostgresClusterUpdated of UpdatePostgresClusterResponse
    | UpdatePostgresClusterRejected of UpdatePostgresClusterError

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterRequestBody
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type private UpdatePostgresClusterPlan =
    { Cluster : PostgresCluster
      CurrentCompute : CurrentResourceDeploymentComputeDto
      UpdatedCluster : PostgresCluster
      ReplacementDeploymentCompute : ResourceDeploymentComputeConfiguration option
      CurrentQuotaInput : PostgresQuotaInput
      NextQuotaInput : PostgresQuotaInput }

type private UpdatePostgresClusterPreparation =
    | Completed of UpdatePostgresClusterOutcome
    | ReadyToUpdate of UpdatePostgresClusterPlan

[<RequireQualifiedAccess>]
module private UpdatePostgresClusterWorkflow =
    let private resolveReplacementCompute
        (resolveComputeProfile : ResolveComputeProfile)
        (cluster : PostgresCluster)
        (currentCompute : CurrentResourceDeploymentComputeDto)
        (computeProfileId : string option)
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
                            PostgresResourceDeployment.ResourceType),
                        cancellationToken)

                match TypedResultInterop.computeProfile computeResult with
                | Error error -> return Error(UpdateCompute error)
                | Ok compute
                    when cluster.Spec.DeploymentMode = PostgresDeploymentMode.Operator &&
                         not (String.IsNullOrWhiteSpace compute.RuntimeClassName) ->
                    return
                        Error(
                            UpdateState(
                                UpdatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported cluster.Id))
                | Ok compute -> return Ok(Some compute)
            | _ -> return Ok None
        }

    let prepare
        (loadCluster : LoadPostgresClusterById)
        (resolveStorageProfile : ResolveStorageProfile)
        (resolveComputeProfile : ResolveComputeProfile)
        (loadDeploymentCompute : LoadCurrentResourceDeploymentCompute)
        (request : UpdatePostgresClusterRequest)
        cancellationToken =
        task {
            let! cluster =
                loadCluster.Invoke(
                    request.TenantId,
                    request.ClusterId,
                    cancellationToken)

            match cluster with
            | None ->
                return
                    Completed(
                        UpdatePostgresClusterRejected(
                            UpdateState(PostgresClusterUpdateNotFound request.ClusterId)))
            | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                return
                    Completed(
                        UpdatePostgresClusterRejected(
                            UpdateState(PostgresClusterUpdateUnavailable cluster.Id)))
            | Some cluster
                when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet &&
                     defaultArg request.Body.Instances cluster.Spec.Instances <> 1 ->
                return
                    Completed(
                        UpdatePostgresClusterRejected(
                            UpdateState(
                                PostgresClusterStatefulSetInstancesUnsupported cluster.Id)))
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

                match TypedResultInterop.currentResourceDeploymentCompute currentComputeResult with
                | Error error ->
                    return
                        Completed(
                            UpdatePostgresClusterRejected(UpdateCurrentCompute error))
                | Ok currentCompute ->
                    let! replacementComputeResult =
                        resolveReplacementCompute
                            resolveComputeProfile
                            cluster
                            currentCompute
                            request.Body.ComputeProfileId
                            cancellationToken

                    match replacementComputeResult with
                    | Error error ->
                        return Completed(UpdatePostgresClusterRejected error)
                    | Ok replacementCompute ->
                        let! storageProfileResult =
                            resolveStorageProfile.Invoke(
                                StorageProfileResolutionRequest(
                                    cluster.Spec.StorageProfileId,
                                    currentCompute.DeploymentTargetId,
                                    cluster.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    updatedStorage),
                                cancellationToken)

                        match TypedResultInterop.storageProfile storageProfileResult with
                        | Error error ->
                            return
                                Completed(
                                    UpdatePostgresClusterRejected(UpdateStorage error))
                        | Ok _ ->
                            let updatedCluster =
                                cluster.UpdateDesiredState(updatedInstances, updatedStorage)
                            let currentQuotaInput =
                                PostgresQuotaInput.create
                                    currentCompute.Compute.Requests.CpuMillis
                                    currentCompute.Compute.Requests.MemoryMi
                                    (int64 cluster.Spec.Instances)
                            let (
                                nextRequestsCpuMillis,
                                nextRequestsMemoryMi,
                                replacementDeploymentCompute) =
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
                                      NextQuotaInput = nextQuotaInput })
        }

    let execute
        (quotaAdmissionExecutor : IQuotaAdmissionExecutor)
        (persistUpdatedCluster : PersistUpdatedPostgresCluster)
        (requestDeploymentApply : RequestResourceDeploymentApply)
        (plan : UpdatePostgresClusterPlan)
        cancellationToken =
        quotaAdmissionExecutor.ExecuteOutcomeAsync<
            PostgresQuotaInput,
            UpdatePostgresClusterOutcome>(
            plan.Cluster.TenantId,
            PostgresQuotaResourceKey.create plan.Cluster.Id,
            UpdateQuotaMutation<PostgresQuotaInput>(
                plan.CurrentQuotaInput,
                plan.NextQuotaInput),
            (fun error ->
                UpdatePostgresClusterRejected(UpdateQuota error)),
            (fun transaction ct ->
                task {
                    let! persisted =
                        persistUpdatedCluster.Invoke(
                            transaction.Session,
                            plan.Cluster,
                            plan.UpdatedCluster,
                            ct)

                    match persisted with
                    | PostgresError error ->
                        return
                            transaction.Abort(
                                UpdatePostgresClusterRejected(UpdateConflict error))
                    | PostgresSuccess () ->
                        let! deploymentResult =
                            requestDeploymentApply.Invoke(
                                ResourceDeploymentMutationRequest(
                                    plan.Cluster.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    plan.Cluster.Id,
                                    plan.CurrentCompute.Compute.Profile,
                                    plan.ReplacementDeploymentCompute |> Option.toObj),
                                transaction.Session,
                                ct)

                        match TypedResultInterop.resourceDeploymentMutation deploymentResult with
                        | Error error ->
                            return
                                transaction.Abort(
                                    UpdatePostgresClusterRejected(UpdateDeployment error))
                        | Ok deployment ->
                            return
                                transaction.Commit(
                                    PostgresClusterUpdated(
                                        { Id = plan.Cluster.Id
                                          Placement = deployment }))
                }),
            cancellationToken)

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
                            plan
                            cancellationToken
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
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterUpdated value -> Results.Accepted(value = value)
                    | UpdatePostgresClusterRejected error ->
                        (PostgresHttpMappings.mapUpdateError error).ToHttpResult())
        }
