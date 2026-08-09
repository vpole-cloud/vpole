using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-created")]
public record WebServiceCreatedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    WebServiceSpecSnapshot Spec,
    WebServiceDnsRecordSnapshot DnsRecord,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record WebServiceDnsRecordSnapshot(
    string Id,
    string Domain);
