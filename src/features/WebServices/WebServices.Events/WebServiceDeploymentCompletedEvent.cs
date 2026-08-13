using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service-deployment-completed")]
public sealed record WebServiceDeploymentCompletedEvent(
    string ReleaseId,
    string WebServiceId,
    string ResourceDeploymentId,
    long ResourceRevision,
    long DeploymentGeneration,
    string Operation,
    bool Succeeded,
    bool Retryable,
    string? FailureCode,
    string? FailureMessage,
    string ReleaseOutcome,
    bool ReleaseTerminal) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
