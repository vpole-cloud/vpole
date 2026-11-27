// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Contracts;

public sealed record BackupRuntimeObservation(
    string WalArchivingState,
    DateTimeOffset? WalStatusChangedAt,
    DateTimeOffset? LastSuccessfulBackupAt,
    DateTimeOffset? NextScheduledBackupAt,
    bool NativeScheduleEnabled
);

public union ObserveBackupRuntimeOutcome(
    BackupRuntimeObservation,
    BackupSourceUnavailable,
    BackupNativeOwnershipConflict,
    BackupNativeIdentityUnavailable);
