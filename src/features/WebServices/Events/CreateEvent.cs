using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("create-web-service-requested")]
public record CreateEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    WebServiceSpec Spec,
    DnsRecordShortInfo DnsRecord,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags
) : BaseEvent;