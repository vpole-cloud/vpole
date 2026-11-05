using MassTransit;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Deployments;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Consumers;

public sealed class WebServiceDeploymentConsumer(
    CompleteWebServiceDeployment completeWebServiceDeployment,
    TimeProvider timeProvider)
    : IConsumer<ResourceDeploymentAppliedEvent>,
      IConsumer<ResourceDeploymentFailedEvent>,
      IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context) =>
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
            new WebServiceDeploymentCompletion(
                deploymentEvent.ResourceDeploymentId,
                deploymentEvent.ResourceRevision,
                deploymentEvent.Generation,
                WebServiceReleaseDeploymentOperation.Apply,
                Succeeded: true,
                Retryable: false,
                CompletedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                FailureCode: null,
                FailureMessage: null,
                EventCore: deploymentEvent.EventCore),
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

        if (!TryParseOperation(deploymentEvent.Operation, out var operation))
            return Task.CompletedTask;

        var failureMessage = $"Deployment failed ({deploymentEvent.FailureCode}).";
        return completeWebServiceDeployment(
            deploymentEvent.EventCore.TenantId,
            deploymentEvent.ResourceId,
            new WebServiceDeploymentCompletion(
                deploymentEvent.ResourceDeploymentId,
                deploymentEvent.ResourceRevision,
                deploymentEvent.Generation,
                operation,
                Succeeded: false,
                Retryable: deploymentEvent.Retryable,
                CompletedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                FailureCode: deploymentEvent.FailureCode,
                FailureMessage: failureMessage,
                EventCore: deploymentEvent.EventCore),
            cancellationToken);
    }

    public Task Handle(
        ResourceDeploymentDeletedEvent deploymentEvent,
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
            new WebServiceDeploymentCompletion(
                deploymentEvent.ResourceDeploymentId,
                deploymentEvent.ResourceRevision,
                deploymentEvent.Generation,
                WebServiceReleaseDeploymentOperation.Delete,
                Succeeded: true,
                Retryable: false,
                CompletedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                FailureCode: null,
                FailureMessage: null,
                EventCore: deploymentEvent.EventCore),
            cancellationToken);
    }

    private static bool TryParseOperation(
        string operation,
        out WebServiceReleaseDeploymentOperation parsed) =>
        Enum.TryParse(operation, ignoreCase: true, out parsed) &&
        Enum.IsDefined(parsed);
}
