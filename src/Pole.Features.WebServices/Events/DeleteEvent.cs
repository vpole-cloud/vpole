using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("delete-web-service")]
public record DeleteEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    string DnsRecordId,
    WebServiceSpec Spec,
    ImmutableHashSet<string> Tags
) : BaseEvent, IDeleteDnsRecordEvent;
