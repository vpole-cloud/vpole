// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Events;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Domains.Application.Common;

internal static class DomainMetadataEventExtensions
{
    public static DomainMetadataChangedEvent ToChangedEvent(this TenantDomainZone zone, string? bindingId = null) =>
        new(zone.Id, zone.Revision, bindingId)
        {
            EventCore = EventCore.Create(zone.TenantId),
        };
}
