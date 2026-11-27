// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("pole.web-services/v1/web-service-deployment-completed")]
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
    bool ReleaseTerminal
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
