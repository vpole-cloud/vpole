// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Backups.Events;

[EntityName("pole.backups/v1/configuration-changed")]
public sealed record BackupConfigurationChangedEvent(
    string ResourceId,
    string Kind,
    long Revision,
    EventCore EventCore
) : IPoleEvent;

[EntityName("pole.backups/v1/operation-changed")]
public sealed record BackupOperationChangedEvent(
    string OperationId,
    string State,
    EventCore EventCore
) : IPoleEvent;
