using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Events;

[EntityName("delete-web-service")]
public record DeleteEvent(
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
