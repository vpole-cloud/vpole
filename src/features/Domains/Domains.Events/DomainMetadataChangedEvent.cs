// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Domains.Events;

[EntityName("pole.domains/v1/domain-metadata-changed")]
public sealed record DomainMetadataChangedEvent(
    string ZoneId,
    long ZoneRevision,
    string? BindingId = null
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
