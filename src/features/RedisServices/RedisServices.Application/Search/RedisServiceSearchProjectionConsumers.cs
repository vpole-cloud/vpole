using MassTransit;
using Pole.Features.RedisServices.Events;
using Pole.Features.Search.Contracts;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Search;

public static class RedisServiceSearchProjection
{
    public static SearchSourceType SourceType { get; } = new(
        ResourceTypeId.Create("pole/redis"),
        "RedisService",
        2);

    public static SearchProjectionSnapshot Snapshot(
        string tenantId,
        string redisId,
        string name,
        string resourceGroupId) =>
        new(
            new SearchProjectionIdentity(tenantId, SourceType.ResourceTypeId, redisId),
            name,
            resourceGroupId,
            null,
            [
                new SearchProjectionField(SearchProjectionFieldType.SourceId, redisId),
                new SearchProjectionField(SearchProjectionFieldType.Name, name)
            ]);
}

public sealed class AddRedisServiceToSearchOnRedisServiceCreatedConsumer(
    CreateSearchProjection createSearchProjection)
    : IConsumer<RedisServiceCreatedEvent>
{
    public Task Handle(RedisServiceCreatedEvent message, CancellationToken cancellationToken) =>
        createSearchProjection(
            RedisServiceSearchProjection.Snapshot(
                message.EventCore.TenantId,
                message.RedisId,
                message.Name,
                message.ResourceGroupId),
            cancellationToken);

    public Task Consume(ConsumeContext<RedisServiceCreatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}

public sealed class UpdateRedisServiceInSearchOnRedisServiceUpdatedConsumer(
    UpdateSearchProjection updateSearchProjection)
    : IConsumer<RedisServiceUpdatedEvent>
{
    public Task Handle(RedisServiceUpdatedEvent message, CancellationToken cancellationToken) =>
        updateSearchProjection(
            RedisServiceSearchProjection.Snapshot(
                message.EventCore.TenantId,
                message.RedisId,
                message.Name,
                message.ResourceGroupId),
            cancellationToken);

    public Task Consume(ConsumeContext<RedisServiceUpdatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}

public sealed class RemoveRedisServiceFromSearchOnRedisServiceDeletedConsumer(
    DeleteSearchProjection deleteSearchProjection)
    : IConsumer<RedisServiceDeletedEvent>
{
    public Task Handle(string tenantId, string redisId, CancellationToken cancellationToken) =>
        deleteSearchProjection(
            new SearchProjectionIdentity(
                tenantId,
                RedisServiceSearchProjection.SourceType.ResourceTypeId,
                redisId),
            cancellationToken);

    public Task Consume(ConsumeContext<RedisServiceDeletedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.RedisId,
            context.CancellationToken);
}
