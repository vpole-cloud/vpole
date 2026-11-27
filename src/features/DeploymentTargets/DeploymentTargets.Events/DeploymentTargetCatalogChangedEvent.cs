// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.DeploymentTargets.Events;

[EntityName("pole.deployment-targets/v1/deployment-target-catalog-changed")]
public sealed record DeploymentTargetCatalogChangedEvent(
    string DeploymentTargetId,
    long Revision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
