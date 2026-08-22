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
open Pole.Mediator
open Pole.ResultType
open Pole.Validation

type UpdatePostgresClusterPasswordRequestBody =
    { Password : string }

type UpdatePostgresClusterPasswordResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterPasswordStateError =
    | PostgresClusterPasswordNotFound of clusterId : string
    | PostgresClusterPasswordRotationUnavailable of clusterId : string
    | PostgresClusterPasswordRotationUnsupported of clusterId : string
    | PostgresClusterCredentialNotFound of clusterId : string

type UpdatePostgresClusterPasswordError =
    | PasswordState of UpdatePostgresClusterPasswordStateError
    | PasswordCompute of LoadCurrentResourceDeploymentComputeError
    | PasswordCredential of PostgresCredentialError
    | PasswordDeployment of ResourceDeploymentMutationError
    interface IUnion with
        member this.Value =
            match this with
            | PasswordState error -> box error
            | PasswordCompute error -> box error
            | PasswordCredential error -> box error
            | PasswordDeployment error -> box error

type UpdatePostgresClusterPasswordOutcome =
    | PostgresClusterPasswordUpdated of UpdatePostgresClusterPasswordResponse
    | UpdatePostgresClusterPasswordRejected of UpdatePostgresClusterPasswordError

type UpdatePostgresClusterPasswordRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterPasswordRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterPasswordOutcome>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

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
                let! cluster =
                    loadCluster.Invoke(
                        request.TenantId,
                        request.ClusterId,
                        cancellationToken)

                match cluster with
                | None ->
                    return
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordNotFound request.ClusterId))
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordRotationUnavailable cluster.Id))
                | Some cluster when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet ->
                    return
                        UpdatePostgresClusterPasswordRejected(
                            PasswordState(
                                PostgresClusterPasswordRotationUnsupported cluster.Id))
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
                            UpdatePostgresClusterPasswordRejected(
                                PasswordCompute currentComputeError)
                    else
                        let! credential =
                            loadCredential.Invoke(
                                cluster.TenantId,
                                cluster.Id,
                                cancellationToken)

                        match credential with
                        | None ->
                            return
                                UpdatePostgresClusterPasswordRejected(
                                    PasswordState(
                                        PostgresClusterCredentialNotFound cluster.Id))
                        | Some credential ->
                            let! rotated =
                                rotateCredential.Invoke(
                                    credential,
                                    request.Body.Password,
                                    cancellationToken)

                            match rotated with
                            | PostgresError error ->
                                return
                                    UpdatePostgresClusterPasswordRejected(
                                        PasswordCredential error)
                            | PostgresSuccess replacement ->
                                return!
                                    runMongoTransaction.Invoke(
                                        Func<
                                            MongoTransactionContext<UpdatePostgresClusterPasswordOutcome>,
                                            Threading.CancellationToken,
                                            Threading.Tasks.Task<MongoTransactionDecision<UpdatePostgresClusterPasswordOutcome>>>(
                                            fun transaction ct ->
                                                task {
                                                    let! persisted =
                                                        persistRotatedCredential.Invoke(
                                                            transaction.Session,
                                                            credential,
                                                            replacement,
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
                                                                    cluster.TenantId,
                                                                    PostgresResourceDeployment.ResourceType,
                                                                    cluster.Id,
                                                                    currentCompute.Compute.Profile),
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
                                                                    UpdatePostgresClusterPasswordRejected(
                                                                        PasswordDeployment deploymentError))
                                                        else
                                                            return
                                                                transaction.Commit(
                                                                    PostgresClusterPasswordUpdated(
                                                                        { Id = cluster.Id
                                                                          Placement = deployment }))
                                                }),
                                        cancellationToken)
            }

type UpdatePostgresClusterPasswordRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterPasswordRequest>()
    do
        this.RuleFor(_.ClusterId).MongoObjectId("PostgreSQL cluster ID") |> ignore
        this.RuleFor(_.Body.Password).NotEmpty() |> ignore

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
        [<FromServices>] httpProblems : IHttpProblemMapper,
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return
                result.ToHttpResult(fun outcome ->
                    match outcome with
                    | PostgresClusterPasswordUpdated value -> Results.Accepted(value = value)
                    | UpdatePostgresClusterPasswordRejected error ->
                        httpProblems.Map(error).ToHttpResult())
        }
