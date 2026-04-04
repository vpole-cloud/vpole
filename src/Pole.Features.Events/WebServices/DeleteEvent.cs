using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.WebServices;

[EntityName("delete-web-service")]
public record DeleteEvent(
    string WebServiceId,
    string WebServiceName,
    string ResourceGroupId,
    string? DnsRecordId
) : BaseEvent;
