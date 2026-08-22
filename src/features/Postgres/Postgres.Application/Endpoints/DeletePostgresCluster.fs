namespace Pole.Features.Postgres.Application.Endpoints

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open FluentValidation
open MassTransit
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Lifecycle
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceDeployments.Events
open Pole.Mediator
open Pole.ResultType
open Pole.Validation

type DeletePostgresClusterResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type DeletePostgresClusterStateError =
    | PostgresClusterDeletionNotFound of clusterId : string
    | PostgresClusterDeletionResourceDeploymentMissing of clusterId : string

type DeletePostgresClusterError =
    | DeletePostgresClusterState of DeletePostgresClusterStateError
    | DeletePostgresClusterCompute of LoadCurrentResourceDeploymentComputeError
    | DeletePostgresClusterPersistence of PostgresClusterDeletionConflict
    | DeletePostgresClusterDeployment of ResourceDeploymentMutationError
    | DeletePostgresClusterQuota of QuotaExecutionError
    interface IUnion with
        member this.Value =
            match this with
            | DeletePostgresClusterState error -> box error
            | DeletePostgresClusterCompute error -> box error
            | DeletePostgresClusterPersistence error -> box error
            | DeletePostgresClusterDeployment error -> box error
            | DeletePostgresClusterQuota error -> box error

type DeletePostgresClusterOutcome =
    | PostgresClusterDeletionAccepted of DeletePostgresClusterResponse
    | DeletePostgresClusterRejected of DeletePostgresClusterError

type DeletePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<DeletePostgresClusterOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type DeletePostgresClusterHandler(
    loadCluster : LoadPostgresClusterById,
    beginDeletion : BeginPostgresClusterDeletion,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    requestDeploymentDeletion : RequestResourceDeploymentDeletion,
    loadDeployment : LoadResourceDeployment,
    loadDeploymentCompute : LoadCurrentResourceDeploymentCompute,
    timeProvider : TimeProvider) =
    interface IMediatorOutcomeHandler<DeletePostgresClusterRequest, DeletePostgresClusterOutcome> with
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
                        DeletePostgresClusterRejected(
                            DeletePostgresClusterState(
                                PostgresClusterDeletionNotFound request.ClusterId))
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    let! placement =
                        loadDeployment.Invoke(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id,
                            cancellationToken)

                    if isNull placement then
                        return
                            DeletePostgresClusterRejected(
                                DeletePostgresClusterState(
                                    PostgresClusterDeletionResourceDeploymentMissing cluster.Id))
                    else
                        return
                            PostgresClusterDeletionAccepted(
                                { Id = cluster.Id
                                  Placement = placement })
                | Some cluster ->
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
                        return
                            DeletePostgresClusterRejected(
                                DeletePostgresClusterCompute currentComputeError)
                    else
                        let quotaInput =
                            PostgresQuotaInput.create
                                currentCompute.Compute.Requests.CpuMillis
                                currentCompute.Compute.Requests.MemoryMi
                                (int64 cluster.Spec.Instances)

                        return!
                            quotaAdmissionExecutor.ExecuteOutcomeAsync<
                                PostgresQuotaInput,
                                DeletePostgresClusterOutcome>(
                                cluster.TenantId,
                                PostgresQuotaResourceKey.create cluster.Id,
                                DeleteQuotaMutation<PostgresQuotaInput>(quotaInput),
                                Func<QuotaExecutionError, DeletePostgresClusterOutcome>(fun quotaError ->
                                    DeletePostgresClusterRejected(
                                        DeletePostgresClusterQuota quotaError)),
                                Func<
                                    MongoTransactionContext<DeletePostgresClusterOutcome>,
                                    Threading.CancellationToken,
                                    Task<MongoTransactionDecision<DeletePostgresClusterOutcome>>>(
                                    fun transaction transactionCancellationToken ->
                                        task {
                                            let deleting =
                                                cluster.BeginDeletion(
                                                    timeProvider.GetUtcNow().UtcDateTime)

                                            let! persisted =
                                                beginDeletion.Invoke(
                                                    transaction.Session,
                                                    cluster,
                                                    deleting,
                                                    transactionCancellationToken)

                                            match persisted with
                                            | PostgresError conflict ->
                                                return
                                                    transaction.Abort(
                                                        DeletePostgresClusterRejected(
                                                            DeletePostgresClusterPersistence conflict))
                                            | PostgresSuccess () ->
                                                let! deploymentResult =
                                                    requestDeploymentDeletion.Invoke(
                                                        ResourceDeploymentMutationRequest(
                                                            cluster.TenantId,
                                                            PostgresResourceDeployment.ResourceType,
                                                            cluster.Id,
                                                            currentCompute.Compute.Profile),
                                                        transaction.Session,
                                                        transactionCancellationToken)

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
                                                            DeletePostgresClusterRejected(
                                                                DeletePostgresClusterDeployment deploymentError))
                                                else
                                                    return
                                                        transaction.Commit(
                                                            PostgresClusterDeletionAccepted(
                                                                { Id = cluster.Id
                                                                  Placement = deployment }))
                                        }),
                                cancellationToken)
            }

type FinalizePostgresClusterDeletionConsumer(
    finalizeDeletion : FinalizePostgresClusterDeletion) =
    interface IConsumer<ResourceDeploymentDeletedEvent> with
        member _.Consume(context) =
            let message = context.Message
            if message.ResourceType <> PostgresResourceDeployment.ResourceType.Value then
                Task.CompletedTask
            else
                task {
                    let! result =
                        finalizeDeletion.Invoke(
                            message.EventCore.TenantId,
                            message.ResourceId,
                            context.CancellationToken)

                    match result with
                    | PostgresSuccess () -> ()
                    | PostgresError(PostgresClusterDeletionFinalizationConflict clusterId) ->
                        raise (InvalidOperationException(
                            $"PostgreSQL deletion finalization conflicted for '{clusterId}'."))
                    | PostgresError(PostgresClusterDeletionDeploymentPurgeConflict conflict) ->
                        raise (InvalidOperationException(
                            $"PostgreSQL deployment purge conflicted while finalizing '{conflict.ResourceId}'."))
                }

type DeletePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<DeletePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore

[<AbstractClass; Sealed>]
type DeletePostgresCluster =
    [<Extension>]
    static member MapDeletePostgresClusterEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<DeletePostgresCluster>(
            "/{ClusterId}",
            nameof DeletePostgresCluster.Handle,
            EndpointHttpMethod.Delete)
            .WithName("DeletePostgresCluster")
            .Produces<DeletePostgresClusterResponse>(StatusCodes.Status202Accepted)
        |> ignore
        endpoints

    static member Handle(
        [<AsParameters>] request : DeletePostgresClusterRequest,
        [<FromServices>] mediator : IMediator,
        [<FromServices>] httpProblems : IHttpProblemMapper,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterDeletionAccepted value -> Results.Accepted(value = value)
                    | DeletePostgresClusterRejected error ->
                        httpProblems.Map(error).ToHttpResult())
        }
