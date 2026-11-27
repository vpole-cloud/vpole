// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ComputeProfiles.Events;

[EntityName("pole.compute-profiles/v1/compute-profile-catalog-changed")]
public sealed record ComputeProfileCatalogChangedEvent(
    string ComputeProfileId,
    long Revision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
