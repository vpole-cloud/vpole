using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

 [EntityName("web-service-updated")]
public sealed record WebServiceUpdatedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    WebServiceDnsRecordSnapshot DnsRecord,
    WebServiceUpdatedEventChanges Changes
    ) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record WebServiceUpdatedEventChanges(
    UpdateLog<WebServiceSpecSnapshot> WebServiceSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
