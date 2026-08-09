namespace Pole.Features.Search.Application.Consumers

open System
open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceDeployments.Events
open Pole.Features.Search.Application.Services
open Pole.Features.Search.Domain
open Pole.Workloads

type ResourceDeploymentSearchProjectionConsumer(
    searchService : ISearchService,
    deploymentQueryService : IResourceDeploymentQueryService) =

    let trySourceType (resourceType : string) =
        match resourceType with
        | "pole/web-service" -> Some SearchDocumentType.WebService
        | "pole/redis" -> Some SearchDocumentType.RedisService
        | "pole/postgresql" -> Some SearchDocumentType.PostgresCluster
        | _ -> None

    member _.Handle(
        tenantId : string,
        deploymentId : string,
        resourceType : string,
        resourceId : string,
        generation : int64,
        eventOccurredAtUtc : DateTime,
        cancellationToken : CancellationToken) =
        task {
            match trySourceType resourceType with
            | None -> ()
            | Some sourceType ->
                let! placement =
                    deploymentQueryService.GetAsync(
                        tenantId,
                        ResourceTypeId.Create(resourceType),
                        resourceId,
                        cancellationToken)

                if not (isNull placement)
                   && String.Equals(
                       placement.DeploymentId,
                       deploymentId,
                       StringComparison.Ordinal)
                   && (placement.DesiredGeneration >= generation
                       || placement.ObservedGeneration >= generation) then
                    do!
                        searchService.UpdateOperationalStatus(
                            sourceType,
                            tenantId,
                            resourceId,
                            placement,
                            eventOccurredAtUtc)
                            cancellationToken
        }

type UpdateResourceDeploymentInSearchOnAppliedConsumer(
    searchService : ISearchService,
    deploymentQueryService : IResourceDeploymentQueryService) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        deploymentQueryService)

    interface IConsumer<ResourceDeploymentAppliedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentAppliedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                context.CancellationToken)

type UpdateResourceDeploymentInSearchOnFailedConsumer(
    searchService : ISearchService,
    deploymentQueryService : IResourceDeploymentQueryService) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        deploymentQueryService)

    interface IConsumer<ResourceDeploymentFailedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentFailedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                context.CancellationToken)

type UpdateResourceDeploymentInSearchOnDeletedConsumer(
    searchService : ISearchService,
    deploymentQueryService : IResourceDeploymentQueryService) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        deploymentQueryService)

    interface IConsumer<ResourceDeploymentDeletedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentDeletedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                context.CancellationToken)
