using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/git-provider-connection-changed")]
public sealed record GitProviderConnectionChangedEvent(string ConnectionId) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
