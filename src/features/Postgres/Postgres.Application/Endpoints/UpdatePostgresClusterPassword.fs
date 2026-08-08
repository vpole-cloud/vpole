namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open FluentValidation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Routing
open MongoDB.Driver
open Pole.ASP.Helpers
open Pole.Database
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
open Pole.Features.ResourceDeployments.Application.Common
open Pole.Features.ResourceDeployments.Application.Mutations
open Pole.Features.ResourceDeployments.Contracts
open Pole.Mediator
open Pole.ResultType

type UpdatePostgresClusterPasswordRequestBody =
    { Password : string }

type UpdatePostgresClusterPasswordResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterPasswordRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterPasswordRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterPasswordResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type UpdatePostgresClusterPasswordHandler(
    dbContext : IPostgresDbContext,
    credentialService : IPostgresCredentialService,
    deploymentComputeLookup : IResourceDeploymentComputeLookup,
    deploymentWriter : IResourceDeploymentMutationWriter) =
    interface IMediatorHandler<UpdatePostgresClusterPasswordRequest, UpdatePostgresClusterPasswordResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let clusterFilter =
                    Builders<PostgresCluster>.Filter.And(
                        Builders<PostgresCluster>.Filter.ForTenant(request.TenantId),
                        Builders<PostgresCluster>.Filter.Eq((fun cluster -> cluster.Id), request.ClusterId))
                let! cluster =
                    dbContext.Postgres
                        .Find(clusterFilter)
                        .FirstOrNoneAsync(cancellationToken)

                match cluster with
                | None ->
                    return
                        Failures.NotFound("PostgreSQL cluster not found.", request.ClusterId)
                        |> ResultF.ofFailure
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        Failures.Conflict(
                            "Credentials for a deleting PostgreSQL cluster cannot be rotated.")
                        |> ResultF.ofFailure
                | Some cluster when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet ->
                    return
                        Failures.Conflict(
                            "Password rotation is not supported for StatefulSet PostgreSQL deployments.")
                        |> ResultF.ofFailure
                | Some cluster ->
                    let! currentComputeResult =
                        deploymentComputeLookup.GetCurrentAsync(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id,
                            cancellationToken)

                    match currentComputeResult.TryGetValue() with
                    | struct (false, _, failure) ->
                        return failure |> ResultF.ofFailure
                    | struct (true, currentCompute, _) ->
                        let credentials = PostgresCredentials.collection dbContext
                        let! credential =
                            credentials
                                .Find(fun item ->
                                    item.TenantId = request.TenantId &&
                                    item.PostgresClusterId = cluster.Id)
                                .FirstOrDefaultAsync(cancellationToken)

                        if isNull (box credential) then
                            return
                                Failures.NotFound(
                                    "PostgreSQL credentials were not found.",
                                    cluster.Id)
                                |> ResultF.ofFailure
                        else
                            let! rotatedResult =
                                credentialService.RotateAsync(
                                    credential,
                                    request.Body.Password,
                                    cancellationToken)

                            match rotatedResult.TryGetValue() with
                            | struct (false, _, failure) ->
                                return failure |> ResultF.ofFailure
                            | struct (true, rotated, _) ->
                                return!
                                    dbContext.RunInTransactionAsync(
                                        (fun session ct ->
                                            task {
                                                let! replacement =
                                                    credentials.ReplaceOneAsync(
                                                        session,
                                                        (fun item ->
                                                            item.Id = credential.Id &&
                                                            item.TenantId = request.TenantId &&
                                                            item.PostgresClusterId = cluster.Id &&
                                                            item.Version = credential.Version),
                                                        rotated,
                                                        cancellationToken = ct)

                                                if replacement.ModifiedCount <> 1L then
                                                    return
                                                        Failures.Conflict(
                                                            "The PostgreSQL credential changed while rotation was being accepted.")
                                                        |> ResultF.ofFailure
                                                else
                                                    let! deploymentResult =
                                                        deploymentWriter.RequestApplyAsync(
                                                            ResourceDeploymentMutationCommand(
                                                                cluster.TenantId,
                                                                PostgresResourceDeployment.ResourceType,
                                                                cluster.Id,
                                                                currentCompute.Compute.Profile),
                                                            session,
                                                            ct)

                                                    match deploymentResult.TryGetValue() with
                                                    | struct (false, _, failure) ->
                                                        return failure |> ResultF.ofFailure
                                                    | struct (true, deployment, _) ->
                                                        return
                                                            { Id = cluster.Id
                                                              Placement =
                                                                ResourceDeploymentDtoExtensions.ToPlacementSummaryDto(
                                                                    deployment) }
                                                            |> ResultF.ofValue
                                            }),
                                        cancellationToken = cancellationToken)
            }

type UpdatePostgresClusterPasswordRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterPasswordRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore
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
        cancellationToken : System.Threading.CancellationToken) =
        task {
            let! result = mediator.Send(request, cancellationToken)
            return result.ToHttpResult(fun value -> Results.Accepted(value = value))
        }
