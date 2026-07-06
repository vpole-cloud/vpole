using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-created")]
public record WebServiceCreatedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    WebServiceSpecSnapshot Spec,
    DnsRecordShortInfo DnsRecord,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

