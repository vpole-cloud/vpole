using MassTransit;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Search.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Search;

public static class ResourceGroupSearchProjection
{
    public static SearchSourceType SourceType { get; } = new(
        ResourceTypeId.Create("pole/resource-group"),
        "ResourceGroup",
        0);

    public static SearchProjectionSnapshot Snapshot(
        string tenantId,
        string resourceGroupId,
        string name,
        IEnumerable<string> tags) =>
        new(
            new SearchProjectionIdentity(
                tenantId,
                SourceType.ResourceTypeId,
                resourceGroupId),
            name,
            resourceGroupId,
            name,
            new[] { new SearchProjectionField(SearchProjectionFieldType.Name, name) }
                .Concat(tags.Select(tag =>
                    new SearchProjectionField(SearchProjectionFieldType.Tag, tag)))
                .ToArray());
}

public sealed class AddResourceGroupToSearchOnResourceGroupCreatedConsumer(
    CreateSearchProjection createSearchProjection)
    : IConsumer<ResourceGroupCreatedEvent>
{
    public Task Handle(
        ResourceGroupCreatedEvent message,
        CancellationToken cancellationToken) =>
        createSearchProjection(
            ResourceGroupSearchProjection.Snapshot(
                message.EventCore.TenantId,
                message.ResourceGroupId,
                message.Name,
                message.Tags),
            cancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupCreatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}

public sealed class UpdateResourceGroupInSearchOnResourceGroupUpdatedConsumer(
    UpdateSearchProjection updateSearchProjection)
    : IConsumer<ResourceGroupUpdatedEvent>
{
    public Task Handle(
        ResourceGroupUpdatedEvent message,
        CancellationToken cancellationToken) =>
        updateSearchProjection(
            ResourceGroupSearchProjection.Snapshot(
                message.EventCore.TenantId,
                message.ResourceGroupId,
                message.Name,
                message.Changes.Tags.New),
            cancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupUpdatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}

public sealed class RemoveResourceGroupFromSearchOnResourceGroupDeletedConsumer(
    DeleteSearchProjection deleteSearchProjection)
    : IConsumer<ResourceGroupDeletedEvent>
{
    public Task Handle(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken) =>
        deleteSearchProjection(
            new SearchProjectionIdentity(
                tenantId,
                ResourceGroupSearchProjection.SourceType.ResourceTypeId,
                resourceGroupId),
            cancellationToken);

    public Task Consume(ConsumeContext<ResourceGroupDeletedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.ResourceGroupId,
            context.CancellationToken);
}
