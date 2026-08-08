using MassTransit;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;

namespace Pole.Features.WebServices.Application.Consumers;

public sealed class WebServiceDeploymentConsumer(
    CompleteWebServiceDeployment completeWebServiceDeployment,
    TimeProvider timeProvider)
    : IConsumer<ResourceDeploymentAppliedEvent>,
      IConsumer<ResourceDeploymentFailedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Handle(
        ResourceDeploymentAppliedEvent deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                deploymentEvent.ResourceType,
                WebServiceResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return completeWebServiceDeployment(
            deploymentEvent.EventCore.TenantId,
            deploymentEvent.ResourceId,
            deploymentEvent.Generation,
            new WebServiceDeploymentCompletion(
                Succeeded: true,
                timeProvider.GetUtcNow().UtcDateTime,
                FailureMessage: null,
                deploymentEvent.EventCore),
            cancellationToken);
    }

    public Task Handle(
        ResourceDeploymentFailedEvent deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        if (deploymentEvent.Retryable ||
            !string.Equals(
                deploymentEvent.ResourceType,
                WebServiceResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var failureMessage = $"Deployment failed ({deploymentEvent.FailureCode}).";
        return completeWebServiceDeployment(
            deploymentEvent.EventCore.TenantId,
            deploymentEvent.ResourceId,
            deploymentEvent.Generation,
            new WebServiceDeploymentCompletion(
                Succeeded: false,
                timeProvider.GetUtcNow().UtcDateTime,
                failureMessage,
                deploymentEvent.EventCore),
            cancellationToken);
    }
}
