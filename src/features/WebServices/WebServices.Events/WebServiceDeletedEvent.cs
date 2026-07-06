using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-deleted")]
public record WebServiceDeletedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    string DnsRecordId,
    WebServiceSpecSnapshot Spec,
    ImmutableHashSet<string> Tags
) : IDeleteDnsRecordEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

