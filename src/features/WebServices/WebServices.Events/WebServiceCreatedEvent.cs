using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("pole.web-services/v1/web-service-created")]
public sealed record WebServiceCreatedEvent(
    string WebServiceId,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    long ResourceRevision,
    WebServiceLifecycleState Lifecycle,
    WebServiceSpecSnapshot Spec,
    WebServiceExposureSnapshot Exposure,
    ImmutableHashSet<string> Tags,
    ImmutableList<WebServiceDesiredDeploymentSnapshot> Deployments
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record WebServiceExposureSnapshot(
    string Mode,
    string GeneratedHostnameLabelPrefix
);

public sealed record WebServiceDesiredDeploymentSnapshot(
    string ResourceDeploymentId,
    string DeploymentTargetId,
    long ResourceRevision,
    long DeploymentGeneration,
    string Operation,
    ImmutableList<string> Hostnames
);
