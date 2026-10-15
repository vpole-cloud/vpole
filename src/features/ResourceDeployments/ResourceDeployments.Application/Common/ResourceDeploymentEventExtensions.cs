using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal static class ResourceDeploymentEventExtensions
{
    public static OutboxWrite ToCurrentRequestWrite(
        this ResourceDeployment deployment)
    {
        var core = EventCore.Create(deployment.TenantId);
        return deployment.DesiredOperation switch
        {
            ResourceDeploymentOperation.Apply => OutboxWrite.Create(
                new ResourceDeploymentReconciliationRequestedEvent(
                    deployment.Id,
                    deployment.RuntimeCellId,
                    deployment.DesiredGeneration,
                    deployment.ResourceRevision)
                {
                    ResourceType = deployment.ResourceType,
                    ResourceId = deployment.ResourceId,
                    ResourceGroupId = deployment.ResourceGroupId,
                    EventCore = core
                },
                ResourceDeploymentPendingKey.Apply(
                    deployment.Id,
                    deployment.DesiredGeneration)),
            ResourceDeploymentOperation.Delete => OutboxWrite.Create(
                new ResourceDeploymentDeletionRequestedEvent(
                    deployment.Id,
                    deployment.RuntimeCellId,
                    deployment.DesiredGeneration,
                    deployment.ResourceRevision)
                {
                    ResourceType = deployment.ResourceType,
                    ResourceId = deployment.ResourceId,
                    ResourceGroupId = deployment.ResourceGroupId,
                    EventCore = core
                },
                ResourceDeploymentPendingKey.Delete(
                    deployment.Id,
                    deployment.DesiredGeneration)),
            _ => throw new InvalidOperationException(
                $"Unsupported deployment operation '{deployment.DesiredOperation}'.")
        };
    }

    public static ResourceDeploymentAppliedEvent ToAppliedEvent(
        this ResourceDeployment deployment,
        long generation) =>
        new(
            deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.RuntimeCellId,
            generation,
            deployment.ResourceRevision)
        {
            ResourceGroupId = deployment.ResourceGroupId,
            EventCore = EventCore.Create(deployment.TenantId)
        };

    public static ResourceDeploymentFailedEvent ToFailedEvent(
        this ResourceDeployment deployment,
        FailedResourceDeploymentState failed) =>
        new(
            deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.RuntimeCellId,
            failed.Generation,
            failed.Operation.ToString(),
            failed.FailureCode,
            failed.Retryable,
            deployment.ResourceRevision)
        {
            ResourceGroupId = deployment.ResourceGroupId,
            EventCore = EventCore.Create(deployment.TenantId)
        };

    public static ResourceDeploymentDeletedEvent ToDeletedEvent(
        this ResourceDeployment deployment,
        long generation) =>
        new(
            deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.RuntimeCellId,
            generation,
            deployment.ResourceRevision)
        {
            ResourceGroupId = deployment.ResourceGroupId,
            EventCore = EventCore.Create(deployment.TenantId)
        };

    public static ResourceDeploymentReconciliationStartedEvent ToStartedEvent(this ResourceDeployment deployment) =>
        new(deployment.Id,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.ResourceGroupId,
            deployment.RuntimeCellId,
            deployment.DesiredGeneration,
            deployment.DesiredOperation.ToString(),
            deployment.ResourceRevision)
        {
            EventCore = EventCore.Create(deployment.TenantId)
        };
}
