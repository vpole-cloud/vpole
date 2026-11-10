using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Realtime;

public sealed class NotifyResourceGroupChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<ResourceGroupCreatedEvent>, IConsumer<ResourceGroupUpdatedEvent>, IConsumer<ResourceGroupDeletedEvent>,
    IConsumer<ResourceRegisteredEvent>, IConsumer<ResourceRegistrationUpdatedEvent>,
    IConsumer<ResourceUnregisteredEvent>, IConsumer<IsolationScopeCreatedEvent>,
    IConsumer<IsolationScopeDeletionRequestedEvent>, IConsumer<RuntimeCellReconciliationRequestedEvent>,
    IConsumer<RuntimeCellReadyEvent>, IConsumer<RuntimeCellReconciliationFailedEvent>,
    IConsumer<RuntimeCellDeletionRequestedEvent>, IConsumer<RuntimeCellDeletedEvent>
{
    public Task Consume(ConsumeContext<IsolationScopeCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<IsolationScopeDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupDeletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupUpdatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceRegisteredEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceRegistrationUpdatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceUnregisteredEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RuntimeCellDeletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RuntimeCellDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RuntimeCellReadyEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RuntimeCellReconciliationFailedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RuntimeCellReconciliationRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(ResourceGroupCreatedEvent message, CancellationToken ct = default) => NotifyGroupAsync(
        message.EventCore,
        message.ResourceGroupId,
        message.ResourceRevision,
        ct);

    public Task HandleAsync(ResourceGroupUpdatedEvent message, CancellationToken ct = default) => NotifyGroupAsync(
        message.EventCore,
        message.ResourceGroupId,
        message.ResourceRevision,
        ct);

    public Task HandleAsync(ResourceGroupDeletedEvent message, CancellationToken ct = default) => NotifyGroupAsync(
        message.EventCore,
        message.ResourceGroupId,
        message.ResourceRevision,
        ct);

    public Task HandleAsync(ResourceRegisteredEvent message, CancellationToken ct = default) => NotifyRegistrationAsync(
        message.EventCore,
        message.ResourceGroupId,
        message.ResourceId,
        ct);

    public Task HandleAsync(ResourceRegistrationUpdatedEvent message, CancellationToken ct = default) =>
        NotifyRegistrationAsync(message.EventCore, message.ResourceGroupId, message.ResourceId, ct);

    public Task HandleAsync(ResourceUnregisteredEvent message, CancellationToken ct = default) =>
        NotifyRegistrationAsync(message.EventCore, message.ResourceGroupId, message.ResourceId, ct);

    public Task HandleAsync(IsolationScopeCreatedEvent message, CancellationToken ct = default) =>
        NotifyGroupAsync(message.EventCore, message.ResourceGroupId, null, ct);

    public Task HandleAsync(IsolationScopeDeletionRequestedEvent message, CancellationToken ct = default) =>
        NotifyGroupAsync(message.EventCore, message.ResourceGroupId, null, ct);

    public Task HandleAsync(RuntimeCellReconciliationRequestedEvent message, CancellationToken ct = default) =>
        NotifyCellAsync(message.EventCore, message.Generation, ct);

    public Task HandleAsync(RuntimeCellReadyEvent message, CancellationToken ct = default) =>
        NotifyCellAsync(message.EventCore, message.Generation, ct);

    public Task HandleAsync(RuntimeCellReconciliationFailedEvent message, CancellationToken ct = default) =>
        NotifyCellAsync(message.EventCore, message.Generation, ct);

    public Task HandleAsync(RuntimeCellDeletionRequestedEvent message, CancellationToken ct = default) =>
        NotifyCellAsync(message.EventCore, message.Generation, ct);

    public Task HandleAsync(RuntimeCellDeletedEvent message, CancellationToken ct = default) =>
        NotifyCellAsync(message.EventCore, message.Generation, ct);

    private async Task NotifyGroupAsync(
        EventCore core,
        string groupId,
        long? revision,
        CancellationToken ct)
    {
        await publish(new RealtimeChange("resource-groups", core.TenantId, groupId, groupId, revision), ct);
        // A group mutation invalidates its inventory without claiming a logical resource identity.
        await publish(new RealtimeChange("workspace-inventory", core.TenantId, ResourceGroupId: groupId), ct);
    }

    private async Task NotifyCellAsync(
        EventCore core,
        long generation,
        CancellationToken ct)
    {
        // Historical cell events identify the cell and scope, rather than a group. Broad
        // authorized tenant invalidation works after deletion without a stale owner lookup.
        await publish(new RealtimeChange("resource-groups", core.TenantId, Generation: generation), ct);
        await publish(new RealtimeChange("workspace-inventory", core.TenantId, Generation: generation), ct);
    }

    private async Task NotifyRegistrationAsync(
        EventCore core,
        string groupId,
        string resourceId,
        CancellationToken ct)
    {
        await publish(new RealtimeChange("resource-groups", core.TenantId, groupId, groupId), ct);
        await publish(new RealtimeChange("workspace-inventory", core.TenantId, resourceId, groupId), ct);
    }
}
