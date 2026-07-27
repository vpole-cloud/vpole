using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("isolation-scope-created")]
public sealed record IsolationScopeCreatedEvent(
    string IsolationScopeId,
    string ResourceGroupId,
    string Name,
    bool IsDefault) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
