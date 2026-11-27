// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Common;

internal static class ResourceConnectionEventExtensions
{
    public static ResourceConnectionChangedEvent ToChangedEvent(this ResourceConnection connection) => new(
        connection.Id,
        connection.Revision,
        connection.Generation)
    {
        EventCore = EventCore.Create(connection.TenantId),
    };
}
