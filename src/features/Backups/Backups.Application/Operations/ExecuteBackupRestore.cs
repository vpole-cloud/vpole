using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ExecuteBackupRestore(
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    VerifyBackupRestoreTarget verify
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public async Task<ApplyBackupAttempt> ExecuteAsync(
        BackupOperation operation,
        BackupArchive archive,
        CancellationToken ct)
    {
        if (operation.Work is not RestoreBackupWork work)
            throw new InvalidOperationException("Recovery execution requires a restore operation.");
        var provider = providers.Find(archive.ResourceType);
        if (provider is null)
            return Fail("unsupported-provider");
        BackupRecoveryTarget target = work.Selection switch
        {
            BackupCopySelection copy => new BackupCopyRecoveryTarget(copy.BackupId),
            BackupTimeSelection time => new BackupTimeRecoveryTarget(time.TargetTimeUtc),
        };
        var context = BackupArchiveAccess.Context(archive);
        var observed = await provider.ObserveRestoreAsync(new(operation.Id, context, work.Resource.Id, target), ct);
        return observed switch
        {
            BackupRestoreTargetMissing => await MissingTargetAsync(),
            BackupRestoreInProgress => Pending(),
            BackupRestoreCompleted completed => Complete(completed.ResourceId),
            BackupRestoreIdentityConflict => Fail("restore-identity-conflict"),
            BackupRestorePlacementChanged => Fail("restore-placement-changed"),
            BackupRestoreTargetMismatch => Fail("restore-target-mismatch"),
            BackupRestoreRecoveryFailed => Fail("restore-recovery-failed"),
            BackupNativeIdentityUnavailable => Retry("backup-native-identity-missing"),
        };

        async Task<ApplyBackupAttempt> MissingTargetAsync()
        {
            switch (work.Execution)
            {
                case ProvisioningBackupRestore:
                    return Fail("restore-resource-deleted");
                case RequestedBackupRestore:
                    break;
                case CompletedBackupRestore or FailedBackupRestore:
                    throw new InvalidOperationException("A terminal restore cannot create another target.");
            }

            var available = await verify.ExecuteAsync(archive, work.Selection, ct);
            return available switch
            {
                BackupRestoreTargetAvailable => await StartWithCredentialsAsync(),
                BackupRestoreCopyUnavailable or BackupRestoreTimeUnavailable => Fail("target-unavailable"),
                BackupRestoreTimeUnsupported or BackupSourceUnsupported => Fail("unsupported-provider"),
                BackupCatalogCredentialUnavailable => Retry("catalog-credential-unavailable"),
                BackupCatalogAccessDenied => Retry("backup-catalog-access-denied"),
                BackupCatalogUnavailable => Retry("backup-catalog-unavailable"),
                BackupCatalogUnreadable => Fail("backup-catalog-unreadable"),
                BackupCatalogTimeout => Retry("backup-catalog-timeout"),
            };
        }

        async Task<ApplyBackupAttempt> StartWithCredentialsAsync()
        {
            var runtime = await access.RuntimeAsync(archive, ct);
            return runtime switch
            {
                BackupCredentialProjection projection => await StartAsync(projection),
                BackupArchiveProjectionUnavailable => Retry("workload-credential-unavailable"),
            };
        }

        async Task<ApplyBackupAttempt> StartAsync(BackupCredentialProjection projection)
        {
            var result = await provider.RestoreAsync(
                new(
                    operation.Id,
                    context,
                    projection,
                    new RestoreBackupResource(work.Resource.Id, work.Resource.Name, work.Resource.StorageGiB),
                    target),
                ct);
            return result switch
            {
                BackupRestoreInProgress => Pending(),
                BackupRestoreCompleted completed => Complete(completed.ResourceId),
                BackupRestoreIdentityConflict => Fail("restore-identity-conflict"),
                BackupRestorePlacementChanged => Fail("restore-placement-changed"),
                BackupRestoreTargetMismatch => Fail("restore-target-mismatch"),
                BackupRestoreRecoveryFailed => Fail("restore-recovery-failed"),
                BackupNativeIdentityUnavailable => Retry("backup-native-identity-missing"),
                BackupNativeOwnershipConflict => Fail("backup-resource-ownership-conflict"),
                BackupNativeWriteConflict => Retry("backup-native-write-conflict"),
                BackupRestoreTimePrecisionUnsupported => Fail("restore-target-time-precision-unsupported"),
                BackupRestoreStorageTooSmall => Fail("restore-storage-too-small"),
                BackupRestoreVersionUnsupported => Fail("restore-version-unsupported"),
                BackupRestoreNameAlreadyExists => Fail("restore-name-already-exists"),
                BackupRestoreResourceDeleted => Fail("restore-resource-deleted"),
                BackupRestoreQuotaRejected => Fail("restore-quota-rejected"),
                BackupRestoreTargetRejected => Fail("restore-target-rejected"),
                BackupRestoreComputeRejected => Fail("restore-compute-rejected"),
                BackupRestoreStorageRejected => Fail("restore-storage-rejected"),
                BackupRestoreCredentialRejected => Fail("restore-credential-rejected"),
                BackupRestoreOperationUnavailable => Fail("restore-operation-unavailable"),
                BackupRestorePlacementRejected => Fail("restore-placement-rejected"),
            };
        }
    }

    private static ApplyBackupAttempt Pending() => (current, utcNow) => current.Retry(utcNow, Poll, null);

    private static ApplyBackupAttempt Complete(string resourceId) => (current, utcNow) =>
    {
        if (current.Work is not RestoreBackupWork
            {
                Resource.Id: var expected, Execution: ProvisioningBackupRestore { Target.ResourceId: var recorded }
            } || resourceId != expected || resourceId != recorded)
            throw new InvalidOperationException("Restore completion must identify its recorded resource.");
        return current.CompleteRestore(utcNow);
    };

    private static ApplyBackupAttempt Retry(string code) =>
        (current, utcNow) => current.Retry(utcNow, RetryDelay, code);

    private static ApplyBackupAttempt Fail(string code) => (current, utcNow) => current.Fail(code, utcNow);
}
