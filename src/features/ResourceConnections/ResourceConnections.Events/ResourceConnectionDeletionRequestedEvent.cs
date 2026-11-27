// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceConnections.Events;

[EntityName("pole.resource-connections/v1/resource-connection-deletion-requested")]
public sealed record ResourceConnectionDeletionRequestedEvent(
    string ConnectionId,
    long Generation
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
