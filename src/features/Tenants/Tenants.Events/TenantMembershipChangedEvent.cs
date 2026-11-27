// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Tenants.Events;

/// <summary>Discovery changed for this subject; roles and membership lists remain HTTP data.</summary>
[EntityName("pole.tenants/v1/membership-changed")]
public sealed record TenantMembershipChangedEvent(
    string UserId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
