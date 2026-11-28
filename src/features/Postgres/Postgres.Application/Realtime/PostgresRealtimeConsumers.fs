// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Realtime

open System
open System.Threading
open System.Threading.Tasks
open MassTransit
open Pole.Features.Events.Abstractions
open Pole.Features.Realtime.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Events
open Pole.Features.ResourceDeployments.Events

type NotifyPostgresChangedConsumer(publish: PublishRealtimeChange) =
    let notify (core: EventCore) resourceId groupId (revision: int64) (generation: Nullable<int64>) ct =
        publish.Invoke(
            RealtimeChange("postgres", core.TenantId, resourceId, groupId, Nullable revision, generation),
            ct
        )

    let deployment core resourceType resourceId groupId revision (generation: int64) ct =
        if
            not (isNull resourceType)
            && resourceType <> PostgresResourceDeployment.ResourceType.Value
        then
            Task.CompletedTask
        else
            // Historical requests have no provider identity; invalidate authorized reads broadly.
            notify core (if isNull resourceType then null else resourceId) groupId revision (Nullable generation) ct

    member _.HandleAsync(message: PostgresClusterCreatedEvent, ct: CancellationToken) =
        notify message.EventCore message.ClusterId message.GroupId message.ResourceRevision (Nullable()) ct

    member _.HandleAsync(message: PostgresClusterUpdatedEvent, ct: CancellationToken) =
        notify message.EventCore message.ClusterId message.GroupId message.ResourceRevision (Nullable()) ct

    member _.HandleAsync(message: PostgresClusterDeletedEvent, ct: CancellationToken) =
        notify message.EventCore message.ClusterId message.GroupId message.ResourceRevision (Nullable()) ct

    member _.HandleAsync(message: ResourceDeploymentReconciliationRequestedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    member _.HandleAsync(message: ResourceDeploymentDeletionRequestedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    member _.HandleAsync(message: ResourceDeploymentReconciliationStartedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    member _.HandleAsync(message: ResourceDeploymentAppliedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    member _.HandleAsync(message: ResourceDeploymentFailedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    member _.HandleAsync(message: ResourceDeploymentDeletedEvent, ct: CancellationToken) =
        deployment
            message.EventCore
            message.ResourceType
            message.ResourceId
            message.ResourceGroupId
            message.ResourceRevision
            message.Generation
            ct

    interface IConsumer<PostgresClusterCreatedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<PostgresClusterUpdatedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<PostgresClusterDeletedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentReconciliationRequestedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentDeletionRequestedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentReconciliationStartedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentAppliedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentFailedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)

    interface IConsumer<ResourceDeploymentDeletedEvent> with
        member this.Consume(context) =
            this.HandleAsync(context.Message, context.CancellationToken)
