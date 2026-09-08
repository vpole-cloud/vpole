using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("pole.web-services/v1/web-service-deleted")]
public sealed record WebServiceDeletedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    long ResourceRevision,
    WebServiceSpecSnapshot Spec,
    ImmutableHashSet<string> Tags) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
