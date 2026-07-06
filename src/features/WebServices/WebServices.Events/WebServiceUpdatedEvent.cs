using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Events;

 [EntityName("web-service-updated")]
public sealed record WebServiceUpdatedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    DnsRecordShortInfo DnsRecord,
    WebServiceUpdatedEventChanges Changes
    ) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record WebServiceUpdatedEventChanges(
    UpdateLog<WebServiceSpecSnapshot> WebServiceSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);

