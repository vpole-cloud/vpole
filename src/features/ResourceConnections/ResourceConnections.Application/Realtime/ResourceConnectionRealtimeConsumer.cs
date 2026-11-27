// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Realtime;

public sealed class NotifyResourceConnectionChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<ResourceConnectionCreatedEvent>, IConsumer<ResourceConnectionReconciliationRequestedEvent>,
    IConsumer<ResourceConnectionDeletionRequestedEvent>, IConsumer<ResourceConnectionChangedEvent>
{
    public Task Consume(ConsumeContext<ResourceConnectionChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceConnectionCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceConnectionDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceConnectionReconciliationRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(ResourceConnectionCreatedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "resource-connections",
            message.EventCore.TenantId,
            message.ConnectionId,
            Generation: message.Generation),
        ct);

    public Task HandleAsync(ResourceConnectionReconciliationRequestedEvent message, CancellationToken ct = default) =>
        publish(
            new RealtimeChange(
                "resource-connections",
                message.EventCore.TenantId,
                message.ConnectionId,
                Generation: message.Generation),
            ct);

    public Task HandleAsync(ResourceConnectionDeletionRequestedEvent message, CancellationToken ct = default) =>
        publish(
            new RealtimeChange(
                "resource-connections",
                message.EventCore.TenantId,
                message.ConnectionId,
                Generation: message.Generation),
            ct);

    public Task HandleAsync(ResourceConnectionChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "resource-connections",
            message.EventCore.TenantId,
            message.ConnectionId,
            Revision: message.Revision,
            Generation: message.Generation),
        ct);
}
