// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceConnections.Events;

[EntityName("pole.resource-connections/v1/resource-connection-changed")]
public sealed record ResourceConnectionChangedEvent(
    string ConnectionId,
    long Revision,
    long Generation
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
