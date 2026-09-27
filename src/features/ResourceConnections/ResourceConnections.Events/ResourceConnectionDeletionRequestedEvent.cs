using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceConnections.Events;

[EntityName("pole.resource-connections/v1/resource-connection-deletion-requested")]
public sealed record ResourceConnectionDeletionRequestedEvent(
    string ConnectionId,
    long Generation) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
