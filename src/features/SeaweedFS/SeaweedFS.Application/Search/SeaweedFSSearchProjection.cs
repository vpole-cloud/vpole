using MassTransit;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Events;
using Pole.Features.Search.Contracts;

namespace Pole.Features.SeaweedFS.Application.Search;

public static class SeaweedFSSearchProjection
{
    public static SearchSourceType SourceType { get; } = new(SeaweedFSResourceDeployment.ResourceType, "SeaweedFS", 8);

    public static SearchProjectionSnapshot Snapshot(SeaweedFSService resource) => new(
        new SearchProjectionIdentity(resource.TenantId, SourceType.ResourceTypeId, resource.Id),
        resource.Name,
        resource.ResourceGroupId,
        null,
        [
            new(SearchProjectionFieldType.Name, resource.Name), new(SearchProjectionFieldType.SourceId, resource.Id),
            .. resource.Tags.Select(tag => new SearchProjectionField(SearchProjectionFieldType.Tag, tag))
        ],
        resource.Revision);
}

public sealed class SeaweedFSSearchConsumer(
    LoadSeaweedFSById load,
    CreateSearchProjection create,
    UpdateSearchProjection update,
    DeleteSearchProjection delete
) : IConsumer<SeaweedFSServiceCreatedEvent>, IConsumer<SeaweedFSServiceChangedEvent>
{
    public async Task Consume(ConsumeContext<SeaweedFSServiceCreatedEvent> context)
    {
        var message = context.Message;
        var resource = await load(message.EventCore.TenantId, message.SeaweedFSId, context.CancellationToken);
        if (resource?.Lifecycle is not null and not DeletedSeaweedFSLifecycle)
            await create(SeaweedFSSearchProjection.Snapshot(resource), context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<SeaweedFSServiceChangedEvent> context)
    {
        var message = context.Message;
        var resource = await load(message.EventCore.TenantId, message.SeaweedFSId, context.CancellationToken);
        if (resource is null || resource.Lifecycle is DeletedSeaweedFSLifecycle)
            await delete(
                new SearchProjectionIdentity(
                    message.EventCore.TenantId,
                    SeaweedFSResourceDeployment.ResourceType,
                    message.SeaweedFSId),
                resource?.Revision ?? message.ResourceRevision,
                context.CancellationToken);
        else
            await update(SeaweedFSSearchProjection.Snapshot(resource), context.CancellationToken);
    }
}
