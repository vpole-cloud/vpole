namespace Pole.Features.Postgres.Application.Search

open System.Threading
open MassTransit
open Pole.Features.Postgres.Events
open Pole.Features.Search.Contracts
open Pole.Workloads

[<RequireQualifiedAccess>]
module PostgresSearchProjection =
    let SourceType =
        SearchSourceType(
            ResourceTypeId.Create("pole/postgresql"),
            "PostgresCluster",
            4)

    let snapshot tenantId clusterId name groupId =
        SearchProjectionSnapshot(
            SearchProjectionIdentity(tenantId, SourceType.ResourceTypeId, clusterId),
            name,
            groupId,
            null,
            [| SearchProjectionField(SearchProjectionFieldType.SourceId, clusterId)
               SearchProjectionField(SearchProjectionFieldType.Name, name) |])

type AddPostgresClusterToSearchOnPostgresClusterCreatedConsumer(
    createSearchProjection : CreateSearchProjection) =

    member _.Handle(event : PostgresClusterCreatedEvent, cancellationToken : CancellationToken) =
        createSearchProjection.Invoke(
            PostgresSearchProjection.snapshot
                event.EventCore.TenantId
                event.ClusterId
                event.Name
                event.GroupId,
            cancellationToken)

    interface IConsumer<PostgresClusterCreatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterCreatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type UpdatePostgresClusterInSearchOnPostgresClusterUpdatedConsumer(
    updateSearchProjection : UpdateSearchProjection) =

    member _.Handle(event : PostgresClusterUpdatedEvent, cancellationToken : CancellationToken) =
        updateSearchProjection.Invoke(
            PostgresSearchProjection.snapshot
                event.EventCore.TenantId
                event.ClusterId
                event.Name
                event.GroupId,
            cancellationToken)

    interface IConsumer<PostgresClusterUpdatedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterUpdatedEvent>) =
            this.Handle(context.Message, context.CancellationToken)

type RemovePostgresClusterFromSearchOnPostgresClusterDeletedConsumer(
    deleteSearchProjection : DeleteSearchProjection) =

    member _.Handle(tenantId : string, clusterId : string, cancellationToken : CancellationToken) =
        deleteSearchProjection.Invoke(
            SearchProjectionIdentity(
                tenantId,
                PostgresSearchProjection.SourceType.ResourceTypeId,
                clusterId),
            cancellationToken)

    interface IConsumer<PostgresClusterDeletedEvent> with
        member this.Consume(context : ConsumeContext<PostgresClusterDeletedEvent>) =
            this.Handle(
                context.Message.EventCore.TenantId,
                context.Message.ClusterId,
                context.CancellationToken)
