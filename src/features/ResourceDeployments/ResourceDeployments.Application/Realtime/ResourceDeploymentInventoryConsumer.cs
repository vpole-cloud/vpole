using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.ResourceDeployments.Application.Realtime;

// These events are already committed with deployment mutations. Inventory consumes their stable
// logical identity and versions without introducing another mutation or provider-type switch.
public sealed class NotifyResourceDeploymentInventoryChangedConsumer(PublishRealtimeChange publish)
    : IConsumer<ResourceDeploymentReconciliationRequestedEvent>, IConsumer<ResourceDeploymentDeletionRequestedEvent>,
      IConsumer<ResourceDeploymentReconciliationStartedEvent>, IConsumer<ResourceDeploymentAppliedEvent>,
      IConsumer<ResourceDeploymentFailedEvent>, IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task HandleAsync(ResourceDeploymentReconciliationRequestedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);
    public Task HandleAsync(ResourceDeploymentDeletionRequestedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);
    public Task HandleAsync(ResourceDeploymentReconciliationStartedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);
    public Task HandleAsync(ResourceDeploymentAppliedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);
    public Task HandleAsync(ResourceDeploymentFailedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);
    public Task HandleAsync(ResourceDeploymentDeletedEvent message, CancellationToken ct = default) =>
        publish(new("workspace-inventory", message.EventCore.TenantId, message.ResourceId, message.ResourceGroupId, message.ResourceRevision, message.Generation), ct);

    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationRequestedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentDeletionRequestedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationStartedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
}
