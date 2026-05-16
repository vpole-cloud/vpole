using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Events;

[EntityName("update-web-service")]
public sealed record UpdateEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    DnsRecordShortInfo DnsRecord,
    UpdateEventChanges Changes
    ) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record UpdateEventChanges(
    UpdateLog<WebServiceSpec> WebServiceSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
