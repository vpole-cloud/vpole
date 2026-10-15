using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceConnections.Events;

[EntityName("pole.resource-connections/v1/resource-connection-changed")]
public sealed record ResourceConnectionChangedEvent(
    string ConnectionId,
    long Revision,
    long Generation) : IPoleEvent
{
    public EventCore EventCore
    {
        get;
        init;
    } = EventCore.Create();
}
