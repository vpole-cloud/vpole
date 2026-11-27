// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Scheduling;

namespace Pole.Features.Backups.Domain;

public static class BackupPolicyFactory
{
    public static BackupPolicy Create(
        string id,
        string tenantId,
        string name,
        string resourceType,
        string resourceId,
        string destinationId,
        string archiveId,
        string schedule,
        bool suspended,
        int retentionDays,
        BackupExecutionOrigin origin,
        DateTime utcNow)
    {
        ValidateSchedule(schedule, retentionDays, utcNow);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);

        return new BackupPolicy(
            id,
            tenantId,
            name,
            resourceType,
            resourceId,
            destinationId,
            archiveId,
            UtcCronSchedule.Parse(schedule).Expression,
            suspended,
            retentionDays,
            1,
            0,
            null,
            new ActiveBackupPolicy(),
            new AvailableBackupDispatch(utcNow, origin),
            0,
            null,
            new ApplyingBackupPolicy());
    }

    internal static void ValidateSchedule(
        string schedule,
        int retentionDays,
        DateTime utcNow)
    {
        _ = UtcCronSchedule.Parse(schedule);
        if (retentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Backup timestamps must be UTC.", nameof(utcNow));
    }
}
