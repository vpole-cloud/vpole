// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitOps.Events;

[EntityName("pole.gitops/v1/source-work-requested")]
public sealed record GitSourceWorkRequested(
    string SourceId,
    EventCore EventCore,
    DateTime? NotBeforeUtc = null
) : IPoleEvent;
