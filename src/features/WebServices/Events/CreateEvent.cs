using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

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
