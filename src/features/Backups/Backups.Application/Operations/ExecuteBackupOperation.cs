using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Progress
    = Pole.Features.Backups.Contracts.BackupProviderResult<Pole.Features.Backups.Contracts.BackupProviderProgress>;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ExecuteBackupOperation(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupOperation> operations,
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    BackupOperations commands,
    MongoRunTransaction transactions,
    LoadRegisteredResource loadSource,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    ILogger<ExecuteBackupOperation> logger
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public async Task<bool> ExecuteAsync(
        string tenantId,
        string operationId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = ObjectId.GenerateNewId().ToString();
        var candidate = await operations
            .Find(
                BackupDispatchFilters.OperationsDue(now) & BackupsFilters<BackupOperation>.ById(tenantId, operationId))
            .SortBy(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
            return false;
        var operation = candidate.Claim(new BackupLease(lockId, now + Lease));
        var claimed = await operations.UpdateOneAsync(
            BackupDispatchFilters.OperationAtWork(candidate),
            Builders<BackupOperation>.Update.Set(o => o.Work, operation.Work),
            cancellationToken: ct);
        if (claimed.ModifiedCount != 1)
            return false;
        await BackupAttemptDiagnostics.RunAsync(
            logger,
            operation.Origin,
            tenantId,
            operationId,
            () => ExecuteClaimedAsync(operation, lockId, ct),
            ct);
        return true;
    }

    private async Task ExecuteClaimedAsync(
        BackupOperation operation,
        string lockId,
        CancellationToken ct)
    {
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(operation.TenantId, operation.ArchiveId))
            .SingleAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Lease / 2);
        var provider = providers.Find(archive.ResourceType);
        var effect = provider is null
            ? new Progress.Failure("unsupported-provider", false)
            : await ExecuteAsync(operation, archive, provider, deadline.Token);
        var updatedAt = clock.GetUtcNow().UtcDateTime;
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var guard = BackupDispatchFilters.ClaimedOperation(operation, lockId, clock.GetUtcNow().UtcDateTime);
                var current = await operations.Find(tx.Session, guard).SingleOrDefaultAsync(token);
                if (current is null)
                    return tx.Abort(false);
                // The provider may have committed the creation receipt after this lease was claimed.
                var updated = effect switch
                {
                    Progress.Failure { Retryable: true } failure => current.Retry(updatedAt, Retry, failure.Code),
                    Progress.Failure { Retryable: false } failure => current.Fail(failure.Code, updatedAt),
                    Progress.Success { Value.Phase: BackupProviderPhase.Succeeded } success => current.Work switch
                    {
                        ManualBackupWork => current.CompleteCopy(success.Value.ProviderBackupId!, updatedAt),
                        RestoreBackupWork => current.CompleteRestore(updatedAt),
                        ScheduledBackupWork => throw new InvalidOperationException(
                            "Scheduled backups are observed separately."),
                    },
                    Progress.Success { Value.Phase: BackupProviderPhase.Running } => current.Work switch
                    {
                        ManualBackupWork => current.CopyRunning(updatedAt, Poll),
                        RestoreBackupWork => current.Retry(updatedAt, Poll, null),
                        ScheduledBackupWork => throw new InvalidOperationException(
                            "Scheduled backups are observed separately."),
                    },
                    Progress.Success => throw new InvalidOperationException("Unsupported provider progress."),
                };
                if ((await operations.ReplaceOneAsync(tx.Session, guard, updated, cancellationToken: token))
                    .MatchedCount != 1)
                    return tx.Abort(false);
                if (updated.State != current.State || updated.FailureCode != current.FailureCode)
                    await commands.Changed(tx.Session, updated, token);
                if (updated.Dispatch is AvailableBackupDispatch)
                    await outbox(tx.Session, [BackupWorkRequests.Operation(updated)], token);
                return tx.Commit(true);
            },
            ct);
    }

    private async Task<Progress> ExecuteAsync(
        BackupOperation operation,
        BackupArchive archive,
        IBackupProvider provider,
        CancellationToken ct)
    {
        if (operation.Kind == BackupOperationKind.Backup && !archive.Active)
            return new Progress.Failure("archive-retired", false);
        var restore = operation.Work as RestoreBackupWork;
        var targetCreated = restore?.Execution is ProvisioningBackupRestore;
        if (targetCreated && await loadSource(
                new(operation.TenantId, ResourceTypeId.Create(archive.ResourceType), restore!.Resource.Id),
                ct) is null)
            return new Progress.Failure("restore-resource-deleted", false);
        var runtime = await access.RuntimeAsync(archive, ct);
        if (runtime is not BackupProviderResult<BackupArchiveContext>.Success success)
            return new Progress.Failure("workload-credential-unavailable", true);
        switch (operation.Work)
        {
            case ManualBackupWork:
                return await provider.RunAsync(new(operation.Id, success.Value), ct);
            case RestoreBackupWork work:
                if (!targetCreated)
                {
                    var invalid = await commands.VerifyTargetAsync(archive, work.Selection, ct);
                    if (invalid is not null)
                        return new Progress.Failure(invalid, invalid != "target-unavailable");
                }

                var backupId = work.Selection is BackupCopySelection copy ? copy.BackupId : null;
                DateTimeOffset? targetTime = work.Selection is BackupTimeSelection time ? time.TargetTimeUtc : null;
                return await provider.RestoreAsync(
                    new(
                        operation.Id,
                        success.Value,
                        work.Resource.Id,
                        work.Resource.Name,
                        backupId,
                        targetTime,
                        work.Resource.StorageGiB),
                    ct);
            case ScheduledBackupWork:
                throw new InvalidOperationException("Scheduled backups are observed separately.");
        }

        throw new InvalidOperationException("Unsupported backup operation.");
    }
}
