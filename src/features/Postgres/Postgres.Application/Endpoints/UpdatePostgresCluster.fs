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
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Multitenancy
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common
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

[<Struct>]
type private PostgresComputeAdoption =
    | Preserve
    | Replace of ResolvedComputeProfile

type UpdatePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    storageProfileResolver : IStorageProfileResolver,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    resolveComputeProfile : ResolveComputeProfile,
    deploymentComputeLookup : IResourceDeploymentComputeLookup,
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
                | Some cluster
                    when cluster.Spec.DeploymentMode = PostgresDeploymentMode.StatefulSet &&
                         defaultArg request.Body.Instances cluster.Spec.Instances <> 1 ->
                    let errors =
                        System.Collections.Generic.Dictionary<string, string[]>()
                    errors["Body.Instances"] <-
                        [| "StatefulSet PostgreSQL supports exactly one instance." |]
                    return
                        Failures.SemanticValidation(errors)
                        |> ResultF.ofFailure
                | Some cluster ->
                    let updatedInstances =
                        defaultArg request.Body.Instances cluster.Spec.Instances
                    let updatedStorage =
                        defaultArg request.Body.Storage cluster.Spec.Storage

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
                        let! replacementComputeResult =
                            task {
                                match request.Body.ComputeProfileId with
                                | Some computeProfileId when not (System.String.IsNullOrWhiteSpace computeProfileId) ->
                                    let! computeResult =
                                        resolveComputeProfile.Invoke(
                                            ComputeProfileResolutionRequest(
                                                computeProfileId,
                                                currentCompute.DeploymentTargetId,
                                                cluster.TenantId,
                                                PostgresResourceDeployment.ResourceType),
                                            cancellationToken)

                                    match computeResult.TryGetValue() with
                                    | struct (false, _, failure) ->
                                        return failure |> ResultF.ofFailure
                                    | struct (true, computeProfile, _)
                                        when cluster.Spec.DeploymentMode = PostgresDeploymentMode.Operator &&
                                             not (System.String.IsNullOrWhiteSpace computeProfile.RuntimeClassName) ->
                                        let errors =
                                            System.Collections.Generic.Dictionary<string, string[]>()
                                        errors["Body.ComputeProfileId"] <-
                                            [| "CloudNativePG compute profiles cannot specify a runtime class." |]
                                        return
                                            Failures.SemanticValidation(errors)
                                            |> ResultF.ofFailure
                                    | struct (true, computeProfile, _) ->
                                        return Replace computeProfile |> ResultF.ofValue
                                | _ ->
                                    return Preserve |> ResultF.ofValue
                            }

                        match replacementComputeResult.TryGetValue() with
                        | struct (false, _, failure) ->
                            return failure |> ResultF.ofFailure
                        | struct (true, replacementCompute, _) ->
                            let! storageProfileResult =
                                storageProfileResolver.ResolveAsync(
                                    StorageProfileResolutionRequest(
                                        cluster.Spec.StorageProfileId,
                                        currentCompute.DeploymentTargetId,
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
                                        updatedStorage)
                                let currentQuotaInput =
                                    PostgresQuotaInput.create
                                        currentCompute.Compute.Requests.CpuMillis
                                        currentCompute.Compute.Requests.MemoryMi
                                        (int64 cluster.Spec.Instances)
                                let nextRequestsCpuMillis, nextRequestsMemoryMi =
                                    match replacementCompute with
                                    | Replace compute ->
                                        compute.Requests.CpuMillis,
                                        compute.Requests.MemoryMi
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
                                    quotaAdmissionExecutor.ExecuteAsync<PostgresQuotaInput, UpdatePostgresClusterResponse>(
                                        cluster.TenantId,
                                        PostgresQuotaResourceKey.create cluster.Id,
                                        UpdateQuotaMutation<PostgresQuotaInput>(
                                            currentQuotaInput,
                                            nextQuotaInput),
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
                                                            ResourceDeploymentMutationRequest(
                                                                cluster.TenantId,
                                                                PostgresResourceDeployment.ResourceType,
                                                                cluster.Id,
                                                                currentCompute.Compute.Profile,
                                                                replacementDeploymentCompute),
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
                                                              Placement = deployment }
                                                            |> ResultF.ofValue
                                            }),
                                        cancellationToken)
            }

type UpdatePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<UpdatePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId).NotEmpty() |> ignore
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
            return result.ToHttpResult(fun value -> Results.Accepted(value = value))
        }
