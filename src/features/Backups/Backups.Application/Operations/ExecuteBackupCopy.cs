using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ExecuteBackupCopy(
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    LoadRegisteredResource loadSource
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public async Task<ApplyBackupAttempt> ExecuteAsync(
        BackupOperation operation,
        BackupArchive archive,
        CancellationToken ct)
    {
        if (operation.Work is not ManualBackupWork work)
            throw new InvalidOperationException("Copy execution requires a manual backup operation.");
        var provider = providers.Find(archive.ResourceType);
        if (provider is null)
            return Fail("unsupported-provider");

        // Observe an existing effect before requiring credentials or deciding whether a new write is allowed.
        var observed = await provider.ObserveCopyAsync(new(operation.Id, BackupArchiveAccess.Context(archive)), ct);
        return observed switch
        {
            BackupCopyMissing => await StartOrWaitAsync(),
            BackupCopyInProgress => Running(),
            BackupCopyCompleted completed => Complete(completed.BackupId),
            BackupSourceUnavailable => await SourceUnavailableAsync(),
            BackupNativeOwnershipConflict => Fail("backup-resource-ownership-conflict"),
            BackupNativeIdentityUnavailable => Retry("backup-native-identity-missing"),
            BackupStatusUnreadable => Retry("backup-status-unreadable"),
            BackupPluginUnavailable => Fail("backup-plugin-unavailable"),
            BackupStorageAccessDenied => Fail("backup-storage-access-denied"),
            BackupStorageUnreachable => Fail("backup-storage-unreachable"),
            BackupDefinitionInvalid => Fail("backup-definition-invalid"),
            BackupExecutionFailed => Fail("backup-execution-failed"),
        };

        async Task<ApplyBackupAttempt> StartOrWaitAsync()
        {
            switch (work.Execution)
            {
                case RunningBackupCopy:
                    // Losing a native CR is not authority to create another physical copy.
                    return Retry("backup-native-copy-missing");
                case RequestedBackupCopy when !archive.Active:
                    return Fail("archive-retired");
                case RequestedBackupCopy:
                    break;
                case CompletedBackupCopy or FailedBackupCopy:
                    throw new InvalidOperationException("A terminal copy cannot execute.");
            }

            var runtime = await access.RuntimeAsync(archive, ct);
            return runtime switch
            {
                BackupCredentialProjection projection => await StartAsync(projection),
                BackupArchiveProjectionUnavailable => Retry("workload-credential-unavailable"),
            };
        }

        async Task<ApplyBackupAttempt> StartAsync(BackupCredentialProjection projection)
        {
            var result = await provider.RunAsync(
                new(operation.Id, BackupArchiveAccess.Context(archive), projection),
                ct);
            return result switch
            {
                BackupCopyInProgress => Running(),
                BackupCopyCompleted completed => Complete(completed.BackupId),
                BackupSourceUnavailable => await SourceUnavailableAsync(),
                BackupNativeOwnershipConflict => Fail("backup-resource-ownership-conflict"),
                BackupNativeIdentityUnavailable => Retry("backup-native-identity-missing"),
                BackupNativeWriteConflict => Retry("backup-native-write-conflict"),
                BackupArchiveInactive => Retry("backup-archive-inactive"),
                BackupPluginNotReady => Retry("backup-plugin-not-ready"),
                BackupStatusUnreadable => Retry("backup-status-unreadable"),
                BackupPluginUnavailable => Fail("backup-plugin-unavailable"),
                BackupStorageAccessDenied => Fail("backup-storage-access-denied"),
                BackupStorageUnreachable => Fail("backup-storage-unreachable"),
                BackupDefinitionInvalid => Fail("backup-definition-invalid"),
                BackupExecutionFailed => Fail("backup-execution-failed"),
            };
        }

        async Task<ApplyBackupAttempt> SourceUnavailableAsync() =>
            await loadSource(
                new(operation.TenantId, ResourceTypeId.Create(archive.ResourceType), archive.ResourceId),
                ct) is null
                ? Fail("source-removed-before-backup-completed")
                : Retry("backup-source-unavailable");
    }

    private static ApplyBackupAttempt Running() => (current, utcNow) => current.CopyRunning(utcNow, Poll);

    private static ApplyBackupAttempt Complete(string backupId) =>
        (current, utcNow) => current.CompleteCopy(backupId, utcNow);

    private static ApplyBackupAttempt Retry(string code) =>
        (current, utcNow) => current.Retry(utcNow, RetryDelay, code);

    private static ApplyBackupAttempt Fail(string code) => (current, utcNow) => current.Fail(code, utcNow);
}
