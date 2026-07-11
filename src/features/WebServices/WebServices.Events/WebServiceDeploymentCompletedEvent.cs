using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-deployment-completed")]
public sealed record WebServiceDeploymentCompletedEvent(
    string ReleaseId,
    string WebServiceId,
    bool Succeeded,
    string? FailureMessage) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
