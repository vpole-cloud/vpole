using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-deployment-requested")]
public sealed record WebServiceDeploymentRequestedEvent(
    string ReleaseId,
    int Revision,
    string Action,
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    DnsRecordShortInfo DnsRecord,
    WebServiceSpecSnapshot Spec
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
