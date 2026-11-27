// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Users.Events;

[EntityName("pole.users/v1/user-created")]
public sealed record UserCreatedEvent(
    string UserId,
    string Email,
    string? FirstName,
    string? LastName,
    string DefaultTenantId,
    string DefaultTenantName
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
