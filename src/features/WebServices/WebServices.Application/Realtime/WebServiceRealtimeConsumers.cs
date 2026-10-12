using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Realtime;

public sealed class NotifyWebServiceChangedConsumer(PublishRealtimeChange publish, LoadWebServiceById loadWebService)
    : IConsumer<WebServiceCreatedEvent>,
      IConsumer<WebServiceUpdatedEvent>,
      IConsumer<WebServiceDeletedEvent>,
      IConsumer<WebServiceDeploymentCompletedEvent>,
      IConsumer<ResourceDeploymentReconciliationRequestedEvent>,
      IConsumer<ResourceDeploymentDeletionRequestedEvent>,
      IConsumer<ResourceDeploymentReconciliationStartedEvent>,
      IConsumer<ResourceDeploymentAppliedEvent>,
      IConsumer<ResourceDeploymentFailedEvent>,
      IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task HandleAsync(WebServiceCreatedEvent message, CancellationToken ct = default) =>
        publish(new RealtimeChange("web-services", message.EventCore.TenantId, message.WebServiceId,
            message.ResourceGroupId, message.ResourceRevision), ct);

    public Task HandleAsync(WebServiceUpdatedEvent message, CancellationToken ct = default) =>
        publish(new RealtimeChange("web-services", message.EventCore.TenantId, message.WebServiceId,
            message.ResourceGroupId, message.ResourceRevision), ct);

    public Task HandleAsync(WebServiceDeletedEvent message, CancellationToken ct = default) =>
        publish(new RealtimeChange("web-services", message.EventCore.TenantId, message.WebServiceId,
            message.ResourceGroupId, message.ResourceRevision), ct);

    public async Task HandleAsync(WebServiceDeploymentCompletedEvent message, CancellationToken ct = default)
    {
        var service = await loadWebService(message.EventCore.TenantId, message.WebServiceId, ct);
        await publish(new RealtimeChange("web-services", message.EventCore.TenantId, message.WebServiceId,
            service?.ResourceGroupId, message.ResourceRevision, message.DeploymentGeneration), ct);
    }

    public Task HandleAsync(ResourceDeploymentReconciliationRequestedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    public Task HandleAsync(ResourceDeploymentDeletionRequestedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    public Task HandleAsync(ResourceDeploymentReconciliationStartedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    public Task HandleAsync(ResourceDeploymentAppliedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    public Task HandleAsync(ResourceDeploymentFailedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    public Task HandleAsync(ResourceDeploymentDeletedEvent message, CancellationToken ct = default) =>
        NotifyDeploymentAsync(message.EventCore, message.ResourceType, message.ResourceId, message.ResourceGroupId,
            message.ResourceRevision, message.Generation, ct);

    private Task NotifyDeploymentAsync(EventCore core, string? resourceType, string? resourceId, string? groupId,
        long revision, long generation, CancellationToken ct)
    {
        if (resourceType is not null && resourceType != WebServiceResourceDeployment.ResourceType.Value)
            return Task.CompletedTask;
        // Historical requests have no logical identity; refresh authorized WebService reads in this tenant.
        return publish(new RealtimeChange("web-services", core.TenantId,
            resourceType is null ? null : resourceId, groupId, revision, generation), ct);
    }

    public Task Consume(ConsumeContext<WebServiceCreatedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<WebServiceUpdatedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<WebServiceDeletedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<WebServiceDeploymentCompletedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationRequestedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentDeletionRequestedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentReconciliationStartedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
}
