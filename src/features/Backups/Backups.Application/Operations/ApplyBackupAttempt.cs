// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

/// <summary>A pure transition applied to the current operation after its durable lease is verified.</summary>
public delegate BackupOperation ApplyBackupAttempt(BackupOperation current, DateTime utcNow);
