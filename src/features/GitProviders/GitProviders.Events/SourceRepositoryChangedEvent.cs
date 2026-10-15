using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/source-repository-changed")]
public sealed record SourceRepositoryChangedEvent(
    string SourceRepositoryId,
    string ConnectionId) : IPoleEvent
{
    public EventCore EventCore
    {
        get;
        init;
    } = EventCore.Create();
}
