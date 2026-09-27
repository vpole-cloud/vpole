using MassTransit;
using Pole.Features.ResourceConnections.Application.Reconciliation;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Consumers;

public sealed class ResourceConnectionCreatedConsumer(
    ResourceConnectionReconciler reconciler)
    : IConsumer<ResourceConnectionCreatedEvent>
{
    public Task Consume(ConsumeContext<ResourceConnectionCreatedEvent> context) =>
        reconciler.ReconcileAsync(
            context.Message.EventCore.TenantId,
            context.Message.ConnectionId,
            context.Message.Generation,
            context.CancellationToken);
}

public sealed class ResourceConnectionDeletionConsumer(
    ResourceConnectionReconciler reconciler)
    : IConsumer<ResourceConnectionDeletionRequestedEvent>
{
    public Task Consume(ConsumeContext<ResourceConnectionDeletionRequestedEvent> context) =>
        reconciler.ReconcileAsync(
            context.Message.EventCore.TenantId,
            context.Message.ConnectionId,
            context.Message.Generation,
            context.CancellationToken);
}

public sealed class ResourceConnectionRefreshConsumer(
    ResourceConnectionReconciler reconciler)
    : IConsumer<ResourceConnectionReconciliationRequestedEvent>
{
    public Task Consume(ConsumeContext<ResourceConnectionReconciliationRequestedEvent> context) =>
        reconciler.ReconcileAsync(
            context.Message.EventCore.TenantId,
            context.Message.ConnectionId,
            context.Message.Generation,
            context.CancellationToken);
}
