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
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Application.Common
open Pole.Features.ResourceDeployments.Application.Mutations
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Mediator
open Pole.ResultType

type UpdatePostgresClusterRequestBody =
    { Instances : int option
      Storage : int option
      Resources : Resources option }

type UpdatePostgresClusterResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type UpdatePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromBody>]
      Body : UpdatePostgresClusterRequestBody
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<UpdatePostgresClusterResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type UpdatePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    storageProfileResolver : IStorageProfileResolver,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    deploymentQuery : IResourceDeploymentQueryService,
    deploymentWriter : IResourceDeploymentMutationWriter) =
    interface IMediatorHandler<UpdatePostgresClusterRequest, UpdatePostgresClusterResponse> with
        member _.HandleAsync(request, cancellationToken) =
            task {
                let filter =
                    Builders<PostgresCluster>.Filter.And(
                        Builders<PostgresCluster>.Filter.ForTenant(request.TenantId),
                        Builders<PostgresCluster>.Filter.Eq((fun cluster -> cluster.Id), request.ClusterId))

                let! cluster =
                    dbContext.Postgres
                        .Find(filter)
                        .FirstOrNoneAsync(cancellationToken)

                match cluster with
                | None ->
                    return
                        Failures.NotFound("PostgreSQL cluster not found.", request.ClusterId)
                        |> ResultF.ofFailure
                | Some cluster when (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) ->
                    return
                        Failures.Conflict("A deleting PostgreSQL cluster cannot be updated.")
                        |> ResultF.ofFailure
                | Some cluster ->
                    let updatedInstances =
                        defaultArg request.Body.Instances cluster.Spec.Instances
                    let updatedStorage =
                        defaultArg request.Body.Storage cluster.Spec.Storage
                    let updatedResources =
                        defaultArg request.Body.Resources cluster.Spec.Resources

                    if cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet &&
                       updatedInstances <> 1 then
                        let errors =
                            System.Collections.Generic.Dictionary<string, string[]>()
                        errors["Body.Instances"] <-
                            [| "StatefulSet PostgreSQL supports exactly one instance." |]
                        return
                            Failures.ValidationFailure(errors)
                            |> ResultF.ofFailure
                    else
                        let! placement =
                            deploymentQuery.GetAsync(
                                cluster.TenantId,
                                ResourceDeploymentResourceTypes.PostgreSql,
                                cluster.Id,
                                cancellationToken)

                        if isNull placement then
                            return
                                Failures.Conflict(
                                    "The PostgreSQL resource deployment is unavailable.")
                                |> ResultF.ofFailure
                        else
                            let! storageProfileResult =
                                storageProfileResolver.ResolveAsync(
                                    StorageProfileResolutionRequest(
                                        cluster.Spec.StorageProfileId,
                                        placement.DeploymentTargetId,
                                        cluster.TenantId,
                                        ResourceType.Postgres,
                                        updatedStorage),
                                    cancellationToken)

                            match storageProfileResult.TryGetValue() with
                            | struct (false, _, failure) ->
                                return failure |> ResultF.ofFailure
                            | struct (true, _, _) ->
                                let updatedCluster =
                                    cluster.UpdateDesiredState(
                                        updatedInstances,
                                        updatedStorage,
                                        updatedResources)

                                return!
                                    quotaAdmissionExecutor.ExecuteAsync<PostgresClusterSpec, UpdatePostgresClusterResponse>(
                                        cluster.TenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        UpdateQuotaMutation<PostgresClusterSpec>(
                                            cluster.Spec,
                                            updatedCluster.Spec),
                                        (fun session ct ->
                                            task {
                                                let! replacement =
                                                    dbContext.Postgres.ReplaceOneAsync(
                                                        session,
                                                        Builders<PostgresCluster>.Filter.And(
                                                            filter,
                                                            Builders<PostgresCluster>.Filter.Eq(
                                                                (fun item -> item.Revision),
                                                                cluster.Revision)),
                                                        updatedCluster,
                                                        cancellationToken = ct)

                                                if replacement.ModifiedCount <> 1L then
                                                    return
                                                        Failures.Conflict(
                                                            "The PostgreSQL cluster changed while the update was being accepted.")
                                                        |> ResultF.ofFailure
                                                else
                                                    let! deploymentResult =
                                                        deploymentWriter.RequestApplyAsync(
                                                            ResourceDeploymentMutationCommand(
                                                                cluster.TenantId,
                                                                ResourceDeploymentResourceTypes.PostgreSql,
                                                                cluster.Id),
                                                            session,
                                                            ct)

                                                    match deploymentResult.TryGetValue() with
                                                    | struct (false, _, failure) ->
                                                        return failure |> ResultF.ofFailure
                                                    | struct (true, deployment, _) ->
                                                        do!
                                                            dbContext.StoreEventAsync(
                                                                PostgresClusterEvents.updated
                                                                    cluster
                                                                    updatedCluster,
                                                                session,
                                                                ct)
                                                        return
                                                            { Id = cluster.Id
                                                              Placement =
                                                                ResourceDeploymentDtoExtensions.ToPlacementSummaryDto(
                                                                    deployment) }
                                                            |> ResultF.ofValue
                                            }),
                                        cancellationToken)
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore
        this.When(
            (fun request -> Option.isSome request.Body.Resources),
            (fun () ->
                this.RuleFor(fun request -> request.Body.Resources.Value)
                    .SetValidator(PostgresResourcesValidator())
                |> ignore))
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
            return result.ToHttpResult(fun value -> Results.Accepted(value = value))
        }
