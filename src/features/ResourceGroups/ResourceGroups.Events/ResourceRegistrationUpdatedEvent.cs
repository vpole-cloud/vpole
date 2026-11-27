// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/resource-registration-updated")]
public sealed record ResourceRegistrationUpdatedEvent(
    string ResourceGroupId,
    string ResourceType,
    string ResourceId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
