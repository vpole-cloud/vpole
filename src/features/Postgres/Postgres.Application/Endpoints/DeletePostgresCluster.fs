namespace Pole.Features.Postgres.Application.Endpoints

open System.Runtime.CompilerServices
open System.Threading.Tasks
open FluentValidation
open MassTransit
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
open Pole.Features.ResourceDeployments.Domain
open Pole.Features.ResourceDeployments.Events
open Pole.Mediator
open Pole.ResultType

type DeletePostgresClusterResponse =
    { Id : string
      Placement : PlacementSummaryDto }

type DeletePostgresClusterRequest =
    { [<FromRoute>]
      ClusterId : string
      [<FromHeader(Name = TenantHeaders.TenantId)>]
      TenantId : string }
    interface IMediatorRequest<DeletePostgresClusterResponse>
    interface ITenantWriteOperation with
        member this.TenantId = this.TenantId

type DeletePostgresClusterHandler(
    dbContext : IPostgresDbContext,
    quotaAdmissionExecutor : IQuotaAdmissionExecutor,
    deploymentWriter : IResourceDeploymentMutationWriter,
    deploymentQuery : IResourceDeploymentQueryService,
    deploymentComputeLookup : IResourceDeploymentComputeLookup,
    timeProvider : System.TimeProvider) =
    interface IMediatorHandler<DeletePostgresClusterRequest, DeletePostgresClusterResponse> with
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
                    let! placement =
                        deploymentQuery.FindOptionAsync(
                            cluster.TenantId,
                            PostgresResourceDeployment.ResourceType,
                            cluster.Id,
                            cancellationToken)

                    match placement with
                    | None ->
                        return
                            Failures.Conflict(
                                "The deleting PostgreSQL cluster has no resource deployment.")
                            |> ResultF.ofFailure
                    | Some placement ->
                        return
                            { Id = cluster.Id
                              Placement = placement }
                            |> ResultF.ofValue
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
                        let quotaInput =
                            PostgresQuotaInput.create
                                currentCompute.Compute.Requests.CpuMillis
                                currentCompute.Compute.Requests.MemoryMi
                                (int64 cluster.Spec.Instances)
                        return!
                            quotaAdmissionExecutor.ExecuteAsync<PostgresQuotaInput, DeletePostgresClusterResponse>(
                                cluster.TenantId,
                                PostgresQuotaResourceKey.create cluster.Id,
                                DeleteQuotaMutation<PostgresQuotaInput>(quotaInput),
                                (fun session ct ->
                                    task {
                                        let deleting =
                                            cluster.BeginDeletion(
                                                timeProvider.GetUtcNow().UtcDateTime)
                                        let! replacement =
                                            dbContext.Postgres.ReplaceOneAsync(
                                                session,
                                                Builders<PostgresCluster>.Filter.And(
                                                    filter,
                                                    Builders<PostgresCluster>.Filter.Eq(
                                                        (fun item -> item.Revision),
                                                        cluster.Revision)),
                                                deleting,
                                                cancellationToken = ct)

                                        if replacement.ModifiedCount <> 1L then
                                            return
                                                Failures.Conflict(
                                                    "The PostgreSQL cluster changed while deletion was being accepted.")
                                                |> ResultF.ofFailure
                                        else
                                            let! deploymentResult =
                                                deploymentWriter.RequestDeletionAsync(
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
                                cancellationToken)
            }

type FinalizePostgresClusterDeletionConsumer(
    dbContext : IPostgresDbContext,
    deploymentDbContext : IResourceDeploymentDbContext) =
    interface IConsumer<ResourceDeploymentDeletedEvent> with
        member _.Consume(context) =
            let message = context.Message
            if message.ResourceType <> PostgresResourceDeployment.ResourceType.Value then
                Task.CompletedTask
            else
                dbContext.RunInTransactionAsync(
                    (fun session ct ->
                        task {
                            let clusterFilter =
                                Builders<PostgresCluster>.Filter.And(
                                    Builders<PostgresCluster>.Filter.ForTenant(
                                        message.EventCore.TenantId),
                                    Builders<PostgresCluster>.Filter.Eq(
                                        (fun cluster -> cluster.Id),
                                        message.ResourceId))
                            let! cluster =
                                dbContext.Postgres
                                    .Find(session, clusterFilter)
                                    .FirstOrDefaultAsync(ct)

                            if isNull (box cluster) ||
                               not (cluster.Lifecycle :? DeletingPostgresClusterLifecycle) then
                                return Result.Success() :> Result
                            else
                                let! deployments =
                                    deploymentDbContext.ResourceDeployments
                                        .Find(
                                            session,
                                            fun deployment ->
                                                deployment.TenantId = message.EventCore.TenantId &&
                                                deployment.ResourceType =
                                                    PostgresResourceDeployment.ResourceType.Value &&
                                                deployment.ResourceId = message.ResourceId)
                                        .ToListAsync(ct)

                                if deployments.Count = 0 ||
                                   deployments
                                   |> Seq.exists (fun deployment ->
                                       not (deployment.State :? DeletedResourceDeploymentState)) then
                                    return Result.Success() :> Result
                                else
                                    let! deletedCluster =
                                        dbContext.Postgres.DeleteOneAsync(
                                            session,
                                            Builders<PostgresCluster>.Filter.And(
                                                clusterFilter,
                                                Builders<PostgresCluster>.Filter.Eq(
                                                    (fun item -> item.Revision),
                                                    cluster.Revision)),
                                            cancellationToken = ct)

                                    if deletedCluster.DeletedCount <> 1L then
                                        return Result.Success() :> Result
                                    else
                                        let! _ =
                                            (PostgresCredentials.collection dbContext)
                                                .DeleteOneAsync(
                                                    session,
                                                    (fun credential ->
                                                        credential.TenantId =
                                                            message.EventCore.TenantId &&
                                                        credential.PostgresClusterId =
                                                            message.ResourceId),
                                                    cancellationToken = ct)
                                        let! _ =
                                            deploymentDbContext.ResourceDeployments
                                                .DeleteManyAsync(
                                                    session,
                                                    (fun deployment ->
                                                        deployment.TenantId =
                                                            message.EventCore.TenantId &&
                                                        deployment.ResourceType =
                                                            PostgresResourceDeployment.ResourceType.Value &&
                                                        deployment.ResourceId =
                                                            message.ResourceId),
                                                    cancellationToken = ct)

                                        do!
                                            dbContext.StoreEventAsync(
                                                PostgresClusterEvents.deleted cluster,
                                                session,
                                                ct)
                                        return Result.Success() :> Result
                        }),
                    cancellationToken = context.CancellationToken)

type DeletePostgresClusterRequestValidator() as this =
    inherit AbstractValidator<DeletePostgresClusterRequest>()
    do
        this.RuleFor(_.ClusterId)
            .NotEmpty()
            .WithMessage("Cluster ID must not be empty.")
        |> ignore

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
            return result.ToHttpResult(fun value -> Results.Accepted(value = value))
        }
