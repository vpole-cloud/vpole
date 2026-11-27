// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceDeployments.Events;

/// <summary>No native bindings, specifications, diagnostics, messages or logs enter the outbox.</summary>
[EntityName("pole.resource-deployments/v1/resource-deployment-observation-changed")]
public sealed record ResourceDeploymentObservationChangedEvent(
    string DeploymentId,
    string ResourceId,
    string ResourceGroupId,
    long DeploymentRevision,
    long DesiredGeneration,
    long ObservationVersion
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
