using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.RedisServices.Application.Realtime;

public sealed class NotifyRedisChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<RedisServiceCreatedEvent>, IConsumer<RedisServiceUpdatedEvent>, IConsumer<RedisServiceDeletedEvent>,
    IConsumer<ResourceDeploymentReconciliationRequestedEvent>, IConsumer<ResourceDeploymentDeletionRequestedEvent>,
    IConsumer<ResourceDeploymentReconciliationStartedEvent>, IConsumer<ResourceDeploymentAppliedEvent>,
    IConsumer<ResourceDeploymentFailedEvent>, IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<RedisServiceCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RedisServiceDeletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<RedisServiceUpdatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationStartedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(RedisServiceCreatedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "redis",
            message.EventCore.TenantId,
            message.RedisId,
            message.ResourceGroupId,
            message.ResourceRevision),
        ct);

    public Task HandleAsync(RedisServiceUpdatedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "redis",
            message.EventCore.TenantId,
            message.RedisId,
            message.ResourceGroupId,
            message.ResourceRevision),
        ct);

    public Task HandleAsync(RedisServiceDeletedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "redis",
            message.EventCore.TenantId,
            message.RedisId,
            message.ResourceGroupId,
            message.ResourceRevision),
        ct);

    public Task HandleAsync(ResourceDeploymentReconciliationRequestedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    public Task HandleAsync(ResourceDeploymentDeletionRequestedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    public Task HandleAsync(ResourceDeploymentReconciliationStartedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    public Task HandleAsync(ResourceDeploymentAppliedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    public Task HandleAsync(ResourceDeploymentFailedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    public Task HandleAsync(ResourceDeploymentDeletedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(
            message.EventCore,
            message.ResourceType,
            message.ResourceId,
            message.ResourceGroupId,
            message.ResourceRevision,
            message.Generation,
            ct);

    private Task NotifyDeploymentAsync(
        EventCore core,
        string? type,
        string? resourceId,
        string? groupId,
        long revision,
        long generation,
        CancellationToken ct)
    {
        if (type is not null && type != RedisResourceDeployment.ResourceType.Value)
            return Task.CompletedTask;
        // Historical requests without a provider identity invalidate authorized Redis reads broadly.
        return publish(
            new RealtimeChange("redis", core.TenantId, type is null ? null : resourceId, groupId, revision, generation),
            ct);
    }
}
