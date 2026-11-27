// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Contracts;

/// <summary>Providers may be called repeatedly after an interrupted dispatch. The operation ID identifies a single durable effect.</summary>
public interface IBackupProvider
{
    string ResourceType { get; }
    string DeclarationKind { get; }
    bool SupportsPointInTimeRecovery { get; }
    ValidateBackupScheduleOutcome ValidateSchedule(BackupSchedule schedule);
    Task<DescribeBackupSourceOutcome> DescribeAsync(BackupSourceIdentity source, CancellationToken ct);

    Task<ApplyBackupScheduleOutcome> ApplyScheduleAsync(ApplyBackupScheduleRequest request, CancellationToken ct);

    Task<ReadBackupCatalogOutcome> ListAsync(ListBackupCatalogRequest request, CancellationToken ct);

    Task<ObserveBackupRuntimeOutcome> ObserveAsync(BackupArchiveContext archive, CancellationToken ct);

    Task<ReadBackupHistoryOutcome> ReadHistoryAsync(ReadBackupHistoryRequest request, CancellationToken ct);
    Task<SuspendBackupArchiveOutcome> SuspendArchiveAsync(SuspendBackupArchiveRequest request, CancellationToken ct);

    Task<RunBackupCopyOutcome> RunAsync(BackupCopyRequest request, CancellationToken ct);
    Task<ObserveBackupCopyOutcome> ObserveCopyAsync(ObserveBackupCopyRequest request, CancellationToken ct);
    Task<RestoreBackupOutcome> RestoreAsync(RestoreBackupRequest request, CancellationToken ct);
    Task<ObserveBackupRestoreOutcome> ObserveRestoreAsync(ObserveBackupRestoreRequest request, CancellationToken ct);
}
