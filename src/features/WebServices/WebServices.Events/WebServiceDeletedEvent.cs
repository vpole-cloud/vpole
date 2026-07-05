using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-deleted")]
public record WebServiceDeletedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    string DnsRecordId,
    WebServiceSpec Spec,
    ImmutableHashSet<string> Tags
) : IDeleteDnsRecordEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

