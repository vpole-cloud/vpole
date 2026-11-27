// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/source-repository-changed")]
public sealed record SourceRepositoryChangedEvent(
    string SourceRepositoryId,
    string ConnectionId,
    string TechnicalName,
    long Revision,
    EventCore EventCore
) : IPoleEvent;
