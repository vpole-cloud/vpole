// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/resource-group-created")]
public record ResourceGroupCreatedEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    ImmutableHashSet<string> Tags,
    long ResourceRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
