// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

public sealed record BackupRestoreTargetAvailable;

public sealed record BackupRestoreCopyUnavailable;

public sealed record BackupRestoreTimeUnavailable;

public sealed record BackupRestoreTimeUnsupported;

public union VerifyBackupRestoreTargetOutcome(
    BackupRestoreTargetAvailable,
    BackupRestoreCopyUnavailable,
    BackupRestoreTimeUnavailable,
    BackupRestoreTimeUnsupported,
    BackupSourceUnsupported,
    BackupCatalogCredentialUnavailable,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout);

public sealed class VerifyBackupRestoreTarget(
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    TimeProvider clock
)
{
    public async Task<VerifyBackupRestoreTargetOutcome> ExecuteAsync(
        BackupArchive archive,
        BackupRestoreSelection selection,
        CancellationToken ct)
    {
        var provider = providers.Find(archive.ResourceType);
        if (provider is null)
            return new BackupSourceUnsupported();
        if (selection is BackupTimeSelection && !provider.SupportsPointInTimeRecovery)
            return new BackupRestoreTimeUnsupported();
        if (selection is BackupTimeSelection time && time.TargetTimeUtc > clock.GetUtcNow().UtcDateTime)
            return new BackupRestoreTimeUnavailable();

        var result = await access.ListAsync(archive, provider, ct);
        return result switch
        {
            BackupCatalogContents catalog => selection switch
            {
                BackupCopySelection copy => catalog.Entries.Any(entry => entry.BackupId == copy.BackupId)
                    ? new BackupRestoreTargetAvailable()
                    : new BackupRestoreCopyUnavailable(),
                BackupTimeSelection target => catalog.Entries.Any(entry =>
                    entry.CompletedAt.UtcDateTime <= target.TargetTimeUtc)
                    ? new BackupRestoreTargetAvailable()
                    : new BackupRestoreTimeUnavailable(),
            },
            BackupCatalogCredentialUnavailable error => error,
            BackupCatalogAccessDenied error => error,
            BackupCatalogUnavailable error => error,
            BackupCatalogUnreadable error => error,
            BackupCatalogTimeout error => error,
        };
    }
}
