using MassTransit;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.Search.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Consumers;

public class ResourceDeploymentSearchProjectionConsumer(
    UpdateSearchOperationalStatus updateSearchOperationalStatus,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveSearchSourceType resolveSearchSourceType)
{
    public async Task Handle(
        string tenantId,
        string deploymentId,
        string resourceType,
        string resourceId,
        long generation,
        DateTime eventOccurredAtUtc,
        bool allowMissingDeployment,
        CancellationToken cancellationToken)
    {
        if (!ResourceTypeId.TryCreate(resourceType, out var resourceTypeId) ||
            resolveSearchSourceType(resourceTypeId) is null)
        {
            return;
        }

        var deployments = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: tenantId,
                ResourceType: resourceTypeId,
                ResourceId: resourceId),
            cancellationToken: cancellationToken);
        var eventDeployment = deployments.FirstOrDefault(deployment =>
            string.Equals(
                deployment.DeploymentId,
                deploymentId,
                StringComparison.Ordinal));

        if (eventDeployment is not null &&
            eventDeployment.DesiredGeneration < generation &&
            eventDeployment.ObservedGeneration < generation)
        {
            return;
        }
        if (eventDeployment is null &&
            (!allowMissingDeployment || deployments.Count == 0))
        {
            return;
        }

        await updateSearchOperationalStatus(
            new SearchProjectionIdentity(tenantId, resourceTypeId, resourceId),
            new SearchOperationalStatusSnapshot(
                deployments.Max(deployment => deployment.ResourceRevision),
                deployments.Min(deployment => deployment.ObservedGeneration),
                AggregateStatus(deployments),
                deployments.Select(deployment => deployment.FailureCode)
                    .FirstOrDefault(failureCode => failureCode is not null),
                eventOccurredAtUtc),
            cancellationToken);
    }

    private static string AggregateStatus(
        IReadOnlyList<PlacementSummaryDto> deployments)
    {
        var allStatuses = deployments.Select(deployment => deployment.Status).ToArray();
        var statuses = allStatuses.Where(status => status != ResourceDeploymentStatus.Deleted).ToArray();

        if (statuses.Length == 0 && allStatuses.Length > 0)
            return "Deleted";
        if (statuses.Contains(ResourceDeploymentStatus.DeleteFailed))
            return "DeleteFailed";
        if (statuses.Contains(ResourceDeploymentStatus.Failed))
            return statuses.Contains(ResourceDeploymentStatus.Applied) ? "PartiallyFailed" : "Failed";
        if (statuses.Contains(ResourceDeploymentStatus.Deleting))
            return "Deleting";
        if (statuses.All(status => status == ResourceDeploymentStatus.Applied))
            return "Applied";
        if (statuses.Contains(ResourceDeploymentStatus.Reconciling))
            return "Reconciling";
        if (statuses.Contains(ResourceDeploymentStatus.WaitingForRuntimeCell))
            return "WaitingForRuntimeCell";
        return "Pending";
    }
}

public sealed class UpdateResourceDeploymentInSearchOnAppliedConsumer(
    UpdateSearchOperationalStatus updateSearchOperationalStatus,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveSearchSourceType resolveSearchSourceType)
    : ResourceDeploymentSearchProjectionConsumer(
        updateSearchOperationalStatus,
        loadResourceDeploymentSet,
        resolveSearchSourceType),
      IConsumer<ResourceDeploymentAppliedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.ResourceDeploymentId,
            context.Message.ResourceType,
            context.Message.ResourceId,
            context.Message.Generation,
            context.Message.EventCore.OccurredOn,
            false,
            context.CancellationToken);
}

public sealed class UpdateResourceDeploymentInSearchOnFailedConsumer(
    UpdateSearchOperationalStatus updateSearchOperationalStatus,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveSearchSourceType resolveSearchSourceType)
    : ResourceDeploymentSearchProjectionConsumer(
        updateSearchOperationalStatus,
        loadResourceDeploymentSet,
        resolveSearchSourceType),
      IConsumer<ResourceDeploymentFailedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.ResourceDeploymentId,
            context.Message.ResourceType,
            context.Message.ResourceId,
            context.Message.Generation,
            context.Message.EventCore.OccurredOn,
            false,
            context.CancellationToken);
}

public sealed class UpdateResourceDeploymentInSearchOnDeletedConsumer(
    UpdateSearchOperationalStatus updateSearchOperationalStatus,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    ResolveSearchSourceType resolveSearchSourceType)
    : ResourceDeploymentSearchProjectionConsumer(
        updateSearchOperationalStatus,
        loadResourceDeploymentSet,
        resolveSearchSourceType),
      IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.ResourceDeploymentId,
            context.Message.ResourceType,
            context.Message.ResourceId,
            context.Message.Generation,
            context.Message.EventCore.OccurredOn,
            true,
            context.CancellationToken);
}
