// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SourceBuilds.Events;

[EntityName("pole.source-builds/v1/source-build-configuration-changed")]
public sealed record SourceBuildConfigurationChangedEvent(
    string ConfigurationId,
    string WebServiceId,
    string TechnicalName,
    long Revision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
