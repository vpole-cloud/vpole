// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Events.Abstractions;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Common;

internal static class SourceBuildConfigurationEventExtensions
{
    public static SourceBuildConfigurationChangedEvent ToChangedEvent(this SourceBuildConfiguration configuration) =>
        new(configuration.Id, configuration.WebServiceId, configuration.TechnicalName, configuration.Revision)
        {
            EventCore = EventCore.Create(configuration.TenantId),
        };
}
