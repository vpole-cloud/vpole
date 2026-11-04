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
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts
open Pole.Mediator
open Pole.Validation

type UpdatePostgresClusterPasswordRequestBody =
    { Password : string }

    override _.ToString() = "UpdatePostgresClusterPasswordRequestBody([REDACTED])"

type UpdatePostgresClusterPasswordResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterPasswordOutcome =
    | PostgresClusterPasswordUpdated of UpdatePostgresClusterPasswordResponse
    | UpdatePostgresClusterPasswordRejected of UpdatePostgresClusterPasswordError

type UpdatePostgresClusterPasswordRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterPasswordRequestBody
      [<Pole.ASP.Helpers.FromTenantHeader>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterPasswordOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type private UpdatePostgresClusterPasswordPlan =
    { Cluster : PostgresCluster
      Credential : ManagedSecretValues
      ReplacementCredential : PreparedManagedSecret
      CurrentCompute : CurrentResourceDeploymentComputeDto }

type private UpdatePostgresClusterPasswordPreparation =
    | Completed of UpdatePostgresClusterPasswordOutcome
    | ReadyToRotate of UpdatePostgresClusterPasswordPlan

[<RequireQualifiedAccess>]
module private UpdatePostgresClusterPasswordWorkflow =
    let prepare
        (loadCluster : LoadPostgresClusterById)
        (loadCredential : LoadPostgresCredential)
        (rotateCredential : RotatePostgresCredential)
        (loadDeploymentCompute : LoadCurrentResourceDeploymentCompute)
        (request : UpdatePostgresClusterPasswordRequest)
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
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordNotFound request.ClusterId)))
            | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                return
                    Completed(
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordRotationUnavailable cluster.Id)))
            | Some cluster when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet ->
                return
                    Completed(
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordRotationUnsupported cluster.Id)))
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
                            UpdatePostgresClusterPasswordRejected(
                                PasswordCompute error))
                | Ok currentCompute ->
                    let! credential =
                        loadCredential.Invoke(
                            cluster.TenantId,
                            cluster.Id,
                            cancellationToken)

                    match credential with
                    | PostgresError error ->
                        return Completed(UpdatePostgresClusterPasswordRejected(PasswordCredential error))
                    | PostgresSuccess credential ->
                        let! rotated =
                            rotateCredential.Invoke(
                                cluster.TenantId,
                                cluster.Id,
                                credential,
                                request.Body.Password,
                                cancellationToken)

                        match rotated with
                        | PostgresError error ->
                            return
                                Completed(
                                    UpdatePostgresClusterPasswordRejected(
                                        PasswordCredential error))
                        | PostgresSuccess replacement ->
                            return
                                ReadyToRotate(
                                    { Cluster = cluster
                                      Credential = credential
                                      ReplacementCredential = replacement
                                      CurrentCompute = currentCompute })
        }

    let execute
        (runMongoTransaction : RunMongoOutcomeTransaction<UpdatePostgresClusterPasswordOutcome>)
        (persistRotatedCredential : PersistRotatedPostgresCredential)
        (requestDeploymentApply : RequestResourceDeploymentApply)
        (plan : UpdatePostgresClusterPasswordPlan)
        cancellationToken =
        runMongoTransaction.Invoke(
            (fun transaction ct ->
                task {
                    let! persisted =
                        persistRotatedCredential.Invoke(
                            transaction.Session,
                            plan.Credential,
                            plan.ReplacementCredential,
                            ct)

                    match persisted with
                    | PostgresError error ->
                        return
                            transaction.Abort(
                                UpdatePostgresClusterPasswordRejected(
                                    PasswordCredential error))
                    | PostgresSuccess () ->
                        let! deploymentResult =
                            requestDeploymentApply.Invoke(
                                ResourceDeploymentMutationRequest(
                                    plan.Cluster.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    plan.Cluster.Id,
                                    plan.CurrentCompute.Compute.Profile),
                                transaction.Session,
                                ct)

                        match TypedResultInterop.resourceDeploymentMutation deploymentResult with
                        | Error error ->
                            return
                                transaction.Abort(
                                    UpdatePostgresClusterPasswordRejected(
                                        PasswordDeployment error))
                        | Ok deployment ->
                            return
                                transaction.Commit(
                                    PostgresClusterPasswordUpdated(
                                        { Id = plan.Cluster.Id
                                          Placement = deployment }))
                }),
            cancellationToken)

type UpdatePostgresClusterPasswordHandler(
    loadCluster : LoadPostgresClusterById,
    loadCredential : LoadPostgresCredential,
    rotateCredential : RotatePostgresCredential,
    persistRotatedCredential : PersistRotatedPostgresCredential,
    loadDeploymentCompute : LoadCurrentResourceDeploymentCompute,
    requestDeploymentApply : RequestResourceDeploymentApply,
    runMongoTransaction : RunMongoOutcomeTransaction<UpdatePostgresClusterPasswordOutcome>) =
    interface IMediatorOutcomeHandler<UpdatePostgresClusterPasswordRequest, UpdatePostgresClusterPasswordOutcome> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let! preparation =
                    UpdatePostgresClusterPasswordWorkflow.prepare
                        loadCluster
                        loadCredential
                        rotateCredential
                        loadDeploymentCompute
                        request
                        cancellationToken

                match preparation with
                | Completed outcome -> return outcome
                | ReadyToRotate plan ->
                    return!
                        UpdatePostgresClusterPasswordWorkflow.execute
                            runMongoTransaction
                            persistRotatedCredential
                            requestDeploymentApply
                            plan
                            cancellationToken
            }

type UpdatePostgresClusterPasswordRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterPasswordRequest>()
    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore
        this.RuleFor(_.Body.Password)
            .NotEmpty()
            .Must(fun password -> PostgresManagedSecret.valueBytes password <= PostgresManagedSecret.maximumValueBytes)
            .WithMessage("The UTF-8 password must not exceed 64 KiB.")
        |> ignore

[<AbstractClass; Sealed>]
type UpdatePostgresClusterPassword =
    [<Extension>]
    static member MapUpdatePostgresClusterPasswordEndpoint(endpoints : IEndpointRouteBuilder) =
        endpoints.MapMethod<UpdatePostgresClusterPassword>(
            "/{ClusterId}/password",
            nameof UpdatePostgresClusterPassword.Handle,
            EndpointHttpMethod.Patch)
            .WithName("UpdatePostgresClusterPassword")
            .Produces<UpdatePostgresClusterPasswordResponse>(StatusCodes.Status202Accepted)
        |> ignore
        endpoints

    static member Handle(
        [<AsParameters>] request : UpdatePostgresClusterPasswordRequest,
        [<FromServices>] mediator : IMediator,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterPasswordUpdated value -> Results.Accepted(value = value)
                    | UpdatePostgresClusterPasswordRejected error ->
                        (PostgresHttpMappings.mapPasswordError error).ToHttpResult())
        }
