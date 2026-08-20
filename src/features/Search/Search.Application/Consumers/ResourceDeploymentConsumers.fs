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
    loadResourceDeploymentSet : LoadResourceDeploymentSet) =

    let trySourceType (resourceType : string) =
        match resourceType with
        | "pole/web-service" -> Some SearchDocumentType.WebService
        | "pole/redis" -> Some SearchDocumentType.RedisService
        | "pole/postgresql" -> Some SearchDocumentType.PostgresCluster
        | _ -> None

    let aggregateStatus (deployments : PlacementSummaryDto array) =
        let allStatuses = deployments |> Array.map _.Status
        let statuses = allStatuses |> Array.filter ((<>) "Deleted")
        if statuses.Length = 0 && allStatuses.Length > 0 then "Deleted"
        elif statuses |> Array.exists ((=) "DeleteFailed") then "DeleteFailed"
        elif statuses |> Array.exists ((=) "Failed") then
            if statuses |> Array.exists ((=) "Applied") then "PartiallyFailed" else "Failed"
        elif statuses |> Array.exists ((=) "Deleting") then "Deleting"
        elif statuses |> Array.forall ((=) "Applied") then "Applied"
        elif statuses |> Array.exists ((=) "Reconciling") then "Reconciling"
        elif statuses |> Array.exists ((=) "WaitingForRuntimeCell") then "WaitingForRuntimeCell"
        else "Pending"

    member _.Handle(
        tenantId : string,
        deploymentId : string,
        resourceType : string,
        resourceId : string,
        generation : int64,
        eventOccurredAtUtc : DateTime,
        allowMissingDeployment : bool,
        cancellationToken : CancellationToken) =
        task {
            match trySourceType resourceType with
            | None -> ()
            | Some sourceType ->
                let! current =
                    loadResourceDeploymentSet.Invoke(
                        tenantId,
                        ResourceTypeId.Create(resourceType),
                        resourceId,
                        cancellationToken)

                let deployments = current |> Seq.toArray
                let eventDeployment =
                    deployments
                    |> Array.tryFind (fun deployment ->
                        String.Equals(deployment.DeploymentId, deploymentId, StringComparison.Ordinal))

                match eventDeployment with
                | Some deployment when deployment.DesiredGeneration >= generation
                                       || deployment.ObservedGeneration >= generation ->
                    let status =
                        { DesiredGeneration = deployments |> Array.maxBy _.ResourceRevision |> _.ResourceRevision
                          ObservedGeneration = deployments |> Array.minBy _.ObservedGeneration |> _.ObservedGeneration
                          Status = aggregateStatus deployments
                          FailureCode =
                            deployments
                            |> Array.tryPick (fun item -> Option.ofObj item.FailureCode) }
                    do!
                        searchService.UpdateOperationalStatus(
                            sourceType,
                            tenantId,
                            resourceId,
                            status,
                            eventOccurredAtUtc)
                            cancellationToken
                | None when allowMissingDeployment && deployments.Length > 0 ->
                    let status =
                        { DesiredGeneration = deployments |> Array.maxBy _.ResourceRevision |> _.ResourceRevision
                          ObservedGeneration = deployments |> Array.minBy _.ObservedGeneration |> _.ObservedGeneration
                          Status = aggregateStatus deployments
                          FailureCode =
                            deployments
                            |> Array.tryPick (fun item -> Option.ofObj item.FailureCode) }
                    do!
                        searchService.UpdateOperationalStatus(
                            sourceType,
                            tenantId,
                            resourceId,
                            status,
                            eventOccurredAtUtc)
                            cancellationToken
                | _ -> ()
        }

type UpdateResourceDeploymentInSearchOnAppliedConsumer(
    searchService : ISearchService,
    loadResourceDeploymentSet : LoadResourceDeploymentSet) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        loadResourceDeploymentSet)

    interface IConsumer<ResourceDeploymentAppliedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentAppliedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                false,
                context.CancellationToken)

type UpdateResourceDeploymentInSearchOnFailedConsumer(
    searchService : ISearchService,
    loadResourceDeploymentSet : LoadResourceDeploymentSet) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        loadResourceDeploymentSet)

    interface IConsumer<ResourceDeploymentFailedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentFailedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                false,
                context.CancellationToken)

type UpdateResourceDeploymentInSearchOnDeletedConsumer(
    searchService : ISearchService,
    loadResourceDeploymentSet : LoadResourceDeploymentSet) =
    inherit ResourceDeploymentSearchProjectionConsumer(
        searchService,
        loadResourceDeploymentSet)

    interface IConsumer<ResourceDeploymentDeletedEvent> with
        member this.Consume(context : ConsumeContext<ResourceDeploymentDeletedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ResourceDeploymentId,
                context.Message.ResourceType,
                context.Message.ResourceId,
                context.Message.Generation,
                context.Message.EventCore.OccurredOn,
                true,
                context.CancellationToken)
