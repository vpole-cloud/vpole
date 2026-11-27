// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.StorageProfiles.Events;

[EntityName("pole.storage-profiles/v1/storage-observation-changed")]
public sealed record StorageObservationChangedEvent(
    string DeploymentTargetId,
    long ConnectionVersion,
    long ObservationVersion
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
