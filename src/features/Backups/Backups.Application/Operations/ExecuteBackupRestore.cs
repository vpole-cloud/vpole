using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Progress
    = Pole.Features.Backups.Contracts.BackupProviderResult<Pole.Features.Backups.Contracts.BackupProviderProgress>;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ExecuteBackupRestore(
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    VerifyBackupRestoreTarget verify,
    LoadRegisteredResource loadSource
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    public async Task<ApplyBackupAttempt> ExecuteAsync(
        BackupOperation operation,
        BackupArchive archive,
        CancellationToken ct)
    {
        if (operation.Work is not RestoreBackupWork work)
            throw new InvalidOperationException("Recovery execution requires a restore operation.");
        var provider = providers.Find(archive.ResourceType);
        var effect = provider is null
            ? new Progress.Failure("unsupported-provider", false)
            : await ExecuteProviderAsync(provider);
        return effect switch
        {
            Progress.Failure { Retryable: true } failure => (current, utcNow) =>
                current.Retry(utcNow, Retry, failure.Code),
            Progress.Failure { Retryable: false } failure => (current, utcNow) => current.Fail(failure.Code, utcNow),
            Progress.Success { Value.Phase: BackupProviderPhase.Succeeded } => (current, utcNow) =>
                current.CompleteRestore(utcNow),
            Progress.Success { Value.Phase: BackupProviderPhase.Running } => (current, utcNow) =>
                current.Retry(utcNow, Poll, null),
            Progress.Success => throw new InvalidOperationException("Unsupported restore provider progress."),
        };

        async Task<Progress> ExecuteProviderAsync(IBackupProvider provider)
        {
            if (work.Execution is ProvisioningBackupRestore && await loadSource(
                    new(operation.TenantId, ResourceTypeId.Create(archive.ResourceType), work.Resource.Id),
                    ct) is null)
                return new Progress.Failure("restore-resource-deleted", false);
            var runtime = await access.RuntimeAsync(archive, ct);
            return runtime switch
            {
                BackupCredentialProjection projection => await RestoreAsync(provider, projection),
                BackupArchiveProjectionUnavailable => new Progress.Failure("workload-credential-unavailable", true),
            };
        }

        async Task<Progress> RestoreAsync(IBackupProvider provider, BackupCredentialProjection projection)
        {
            if (work.Execution is RequestedBackupRestore)
            {
                var verification = await verify.ExecuteAsync(archive, work.Selection, ct);
                var rejected = TargetFailure(verification);
                if (rejected is not null)
                    return rejected;
            }

            BackupRecoveryTarget target = work.Selection switch
            {
                BackupCopySelection copy => new BackupCopyRecoveryTarget(copy.BackupId),
                BackupTimeSelection time => new BackupTimeRecoveryTarget(time.TargetTimeUtc),
            };
            return await provider.RestoreAsync(
                new(
                    operation.Id,
                    BackupArchiveAccess.Context(archive),
                    projection,
                    new RestoreBackupResource(work.Resource.Id, work.Resource.Name, work.Resource.StorageGiB),
                    target),
                ct);
        }
    }

    private static Progress.Failure? TargetFailure(VerifyBackupRestoreTargetOutcome target) => target switch
    {
        BackupRestoreTargetAvailable => null,
        BackupRestoreCopyUnavailable or BackupRestoreTimeUnavailable => new("target-unavailable", false),
        BackupRestoreTimeUnsupported or BackupSourceUnsupported => new("unsupported-provider", false),
        BackupCatalogCredentialUnavailable => new("catalog-credential-unavailable", true),
        BackupCatalogAccessDenied => new("backup-catalog-access-denied", true),
        BackupCatalogUnavailable => new("backup-catalog-unavailable", true),
        BackupCatalogUnreadable => new("backup-catalog-unreadable", false),
        BackupCatalogTimeout => new("backup-catalog-timeout", true),
    };
}
