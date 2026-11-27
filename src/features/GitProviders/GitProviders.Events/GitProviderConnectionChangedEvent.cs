// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/git-provider-connection-changed")]
public sealed record GitProviderConnectionChangedEvent(
    string ConnectionId,
    EventCore EventCore
) : IPoleEvent;
