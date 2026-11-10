using MassTransit;

namespace Pole.Features.Outbox.Application;

public interface IOutboxPublisher
{
    Task PublishAsync(
        object message,
        Type messageType,
        CancellationToken cancellationToken);
}

public sealed class MassTransitOutboxPublisher(
    IPublishEndpoint publishEndpoint
) : IOutboxPublisher
{
    public Task PublishAsync(
        object message,
        Type messageType,
        CancellationToken cancellationToken) => publishEndpoint.Publish(message, messageType, cancellationToken);
}
