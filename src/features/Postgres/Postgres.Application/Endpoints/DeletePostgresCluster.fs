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

type DeletePostgresClusterOutcome =
    | PostgresClusterDeletionAccepted of DeletePostgresClusterResponse
    | DeletePostgresClusterRejected of DeletePostgresClusterError

type DeletePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId : string }
    interface IMediatorRequest<DeletePostgresClusterOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type private DeletePostgresClusterPlan =
    { Cluster : PostgresCluster
      CurrentCompute : CurrentResourceDeploymentComputeDto
      QuotaInput : PostgresQuotaInput }

type private DeletePostgresClusterPreparation =
    | Completed of DeletePostgresClusterOutcome
    | ReadyToDelete of DeletePostgresClusterPlan

[<RequireQualifiedAccess>]
module private DeletePostgresClusterWorkflow =
    let prepare
        (loadCluster : LoadPostgresClusterById)
        (loadDeployment : LoadResourceDeployment)
        (loadDeploymentCompute : LoadCurrentResourceDeploymentCompute)
        (request : DeletePostgresClusterRequest)
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
                        DeletePostgresClusterRejected(
                            DeletePostgresClusterState(
                                PostgresClusterDeletionNotFound request.ClusterId)))
            | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                let! placement =
                    loadDeployment.FindOptionAsync(
                        cluster.TenantId,
                        PostgresResourceDeployment.ResourceType,
                        cluster.Id,
                        cancellationToken)

                match placement with
                | None ->
                    return
                        Completed(
                            DeletePostgresClusterRejected(
                                DeletePostgresClusterState(
                                    PostgresClusterDeletionResourceDeploymentMissing cluster.Id)))
                | Some placement ->
                    return
                        Completed(
                            PostgresClusterDeletionAccepted(
                                { Id = cluster.Id
                                  Placement = placement }))
            | Some cluster ->
                let! currentComputeResult =
                    loadDeploymentCompute.Invoke(
                        LoadCurrentResourceDeploymentComputeRequest(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id),
                        cancellationToken)

                match TypedResultInterop.currentResourceDeploymentCompute currentComputeResult with
                | Error error ->
                    return
                        Completed(
                            DeletePostgresClusterRejected(
                                DeletePostgresClusterCompute error))
                | Ok currentCompute ->
                    let quotaInput =
                        PostgresQuotaInput.create
                            currentCompute.Compute.Requests.CpuMillis
                            currentCompute.Compute.Requests.MemoryMi
                            (int64 cluster.Spec.Instances)
                    return
                        ReadyToDelete(
                            { Cluster = cluster
                              CurrentCompute = currentCompute
                              QuotaInput = quotaInput })
        }

    let execute
        (quotaAdmissionExecutor : IQuotaAdmissionExecutor)
        (beginDeletion : BeginPostgresClusterDeletion)
        (requestDeploymentDeletion : RequestResourceDeploymentDeletion)
        (timeProvider : TimeProvider)
        (plan : DeletePostgresClusterPlan)
        cancellationToken =
        quotaAdmissionExecutor.ExecuteOutcomeAsync<
            PostgresQuotaInput,
            DeletePostgresClusterOutcome>(
            ExecuteQuotaAdmissionRequest<PostgresQuotaInput, DeletePostgresClusterOutcome>(
                plan.Cluster.TenantId,
                PostgresQuotaResourceKey.create plan.Cluster.Id,
                DeleteQuotaMutation<PostgresQuotaInput>(plan.QuotaInput),
                (fun quotaError ->
                    DeletePostgresClusterRejected(
                        DeletePostgresClusterQuota quotaError)),
                (fun transaction transactionCancellationToken ->
                    task {
                        let deleting =
                            plan.Cluster.BeginDeletion(
                                timeProvider.GetUtcNow().UtcDateTime)
                        let! persisted =
                            beginDeletion.Invoke(
                                { Current = plan.Cluster; Replacement = deleting },
                                transaction.Session,
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
                                        plan.Cluster.TenantId,
                                        PostgresResourceDeployment.ResourceType,
                                        plan.Cluster.Id,
                                        plan.CurrentCompute.Compute.Profile),
                                    transaction.Session,
                                    transactionCancellationToken)

                            match TypedResultInterop.resourceDeploymentMutation deploymentResult with
                            | Error error ->
                                return
                                    transaction.Abort(
                                        DeletePostgresClusterRejected(
                                            DeletePostgresClusterDeployment error))
                            | Ok deployment ->
                                return
                                    transaction.Commit(
                                        PostgresClusterDeletionAccepted(
                                            { Id = plan.Cluster.Id
                                              Placement = deployment }))
                    })),
            cancellationToken)

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
                let! preparation =
                    DeletePostgresClusterWorkflow.prepare
                        loadCluster
                        loadDeployment
                        loadDeploymentCompute
                        request
                        cancellationToken

                match preparation with
                | Completed outcome -> return outcome
                | ReadyToDelete plan ->
                    return!
                        DeletePostgresClusterWorkflow.execute
                            quotaAdmissionExecutor
                            beginDeletion
                            requestDeploymentDeletion
                            timeProvider
                            plan
                            cancellationToken
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
                    | PostgresError(PostgresClusterDeletionSecretConflict _) ->
                        raise (InvalidOperationException(
                            $"PostgreSQL managed Secret release conflicted while finalizing '{message.ResourceId}'."))
                    | PostgresError(PostgresClusterDeletionRegistrationConflict _) ->
                        raise (InvalidOperationException(
                            $"PostgreSQL resource registration conflicted while finalizing '{message.ResourceId}'."))
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
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterDeletionAccepted value -> Results.Accepted(value = value)
                    | DeletePostgresClusterRejected error ->
                        (PostgresHttpMappings.mapDeleteError error).ToHttpResult())
        }
