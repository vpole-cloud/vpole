// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/isolation-scope-created")]
public sealed record IsolationScopeCreatedEvent(
    string IsolationScopeId,
    string ResourceGroupId,
    string Name,
    bool IsDefault
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.resource-groups/v1/isolation-scope-deletion-requested")]
public sealed record IsolationScopeDeletionRequestedEvent(
    string IsolationScopeId,
    string ResourceGroupId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
