using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Progress
    = Pole.Features.Backups.Contracts.BackupProviderResult<Pole.Features.Backups.Contracts.BackupProviderProgress>;

namespace Pole.Features.Backups.Application.Reconciliation;

public sealed class ApplyBackupPolicy(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupOperation> operations,
    BackupProviderCatalog providers,
    BackupArchiveAccess access,
    BackupOperations commands,
    RetireBackupArchive retireArchive,
    MongoRunTransaction transactions,
    LoadRegisteredResource loadSource,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    ILogger<ApplyBackupPolicy> logger
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public async Task<bool> ExecuteAsync(
        string tenantId,
        string policyId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = ObjectId.GenerateNewId().ToString();
        var candidate = BackupDispatchFilters.PoliciesDue(now) & BackupsFilters<BackupPolicy>.ById(tenantId, policyId);
        var candidatePolicy = await policies.Find(candidate)
            .SortBy(p => p.Dispatch.NextAttemptAtUtc)
            .FirstOrDefaultAsync(ct);
        if (candidatePolicy is null)
            return false;
        var policy = candidatePolicy.Claim(new BackupLease(lockId, now + Lease));
        var claimed = await policies.UpdateOneAsync(
            BackupDispatchFilters.PolicyAtDispatch(candidatePolicy),
            Builders<BackupPolicy>.Update.Set(p => p.Dispatch, policy.Dispatch),
            cancellationToken: ct);
        if (claimed.ModifiedCount != 1)
            return false;
        await BackupAttemptDiagnostics.RunAsync(
            logger,
            policy.Dispatch.Origin,
            tenantId,
            policyId,
            () => ExecuteClaimedAsync(policy, lockId, ct),
            ct);
        return true;
    }

    private async Task ExecuteClaimedAsync(
        BackupPolicy policy,
        string lockId,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Lease / 2);
        var attempt = deadline.Token;
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(policy.TenantId, policy.ArchiveId))
            .SingleAsync(ct);
        var source = await loadSource(
            new(policy.TenantId, ResourceTypeId.Create(policy.ResourceType), policy.ResourceId),
            ct);
        var retiring = policy.Deleting || source is null;
        BackupRuntimeObservation? observation = null;
        Progress effect;
        var provider = providers.Find(policy.ResourceType);
        if (source is null)
            effect = new Progress.Success(new(BackupProviderPhase.Succeeded, null));
        else if (provider is null)
            effect = new Progress.Failure("unsupported-provider", false);
        else if (retiring)
            effect = await provider.ApplyScheduleAsync(
                new(policy.Id, BackupArchiveAccess.Context(archive), new StopBackupPolicy(), policy.Revision),
                attempt);
        else
        {
            var runtime = await access.RuntimeAsync(archive, attempt);
            effect = runtime switch
            {
                BackupCredentialProjection resolved => await provider.ApplyScheduleAsync(
                    new(
                        policy.Id,
                        BackupArchiveAccess.Context(archive),
                        new ConfigureBackupPolicy(
                            new(policy.Schedule, policy.Suspended, policy.RetentionDays),
                            resolved),
                        policy.Revision),
                    attempt),
                BackupArchiveProjectionUnavailable => new Progress.Failure("workload-credential-unavailable", true),
            };
            if (effect is Progress.Success)
            {
                var observed = await provider.ObserveAsync(BackupArchiveAccess.Context(archive), attempt);
                if (observed is BackupProviderResult<BackupRuntimeObservation>.Success observedState)
                    observation = observedState.Value;
                else
                    effect = new Progress.Failure(
                        ((BackupProviderResult<BackupRuntimeObservation>.Failure)observed).Code,
                        true);
            }
        }

        BackupServiceSnapshot? latest = null;
        if (source is not null && provider is not null)
        {
            var description = await provider.DescribeAsync(
                new(policy.TenantId, policy.ResourceType, policy.ResourceId),
                attempt);
            latest = description switch
            {
                BackupServiceSnapshot service => service,
                BackupSourceUnavailable => null,
                BackupSourceUnsupported => null,
            };
        }

        var succeeded = effect is Progress.Success { Value.Phase: BackupProviderPhase.Succeeded };
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var guard = BackupDispatchFilters.ClaimedPolicy(policy, lockId, clock.GetUtcNow().UtcDateTime);
                if (!await policies.Find(tx.Session, guard).AnyAsync(token))
                    return tx.Abort(false);
                if (latest is { } service && service.Revision > archive.SourceRevision)
                    await archives.UpdateOneAsync(
                        tx.Session,
                        BackupsFilters<BackupArchive>.ById(archive.TenantId, archive.Id),
                        Builders<BackupArchive>.Update
                            .Set(a => a.SourceRevision, service.Revision)
                            .Set(a => a.ServiceVersion, service.Version)
                            .Set(a => a.Instances, service.Instances)
                            .Max(a => a.StorageGiB, service.StorageGiB),
                        cancellationToken: token);
                if (retiring && succeeded)
                {
                    if ((await policies.DeleteOneAsync(tx.Session, guard, cancellationToken: token)).DeletedCount == 1)
                    {
                        RetireBackupArchiveRequest retirement = source is null
                            ? new RetireRemovedBackupSource(policy)
                            : new RetireStoppedBackupPolicy(policy);
                        await retireArchive.ExecuteAsync(retirement, tx.Session, token);
                        await outbox(tx.Session, [BackupPolicyNotifications.Removed(policy)], token);
                    }
                }
                else
                {
                    var update = Builders<BackupPolicy>.Update
                        .Set(
                            p => p.Dispatch,
                            new AvailableBackupDispatch(
                                clock.GetUtcNow().UtcDateTime + (effect is Progress.Failure ? Retry : Poll),
                                policy.Dispatch.Origin))
                        .Set(p => p.FailureCode, (effect as Progress.Failure)?.Code);
                    if (observation is not null)
                    {
                        var state = new BackupObservation(
                            observation.WalArchivingState,
                            observation.WalStatusChangedAt?.UtcDateTime,
                            observation.LastSuccessfulBackupAt?.UtcDateTime,
                            observation.NextScheduledBackupAt?.UtcDateTime);
                        update = update.Set(p => p.Observation, state)
                            .Set(
                                p => p.Dispatch,
                                new AvailableBackupDispatch(
                                    clock.GetUtcNow().UtcDateTime + Retry,
                                    policy.Dispatch.Origin));
                    }
                    else if (effect is Progress.Failure && policy.Observation is { } previous)
                        update = update.Set(
                            p => p.Observation,
                            previous with { WalArchivingStatus = "Unknown", NextScheduledAt = null });

                    if (succeeded)
                        update = update.Set(p => p.AppliedRevision, policy.Revision);
                    var applied = await policies.UpdateOneAsync(tx.Session, guard, update, cancellationToken: token);
                    if (applied.MatchedCount != 1)
                        return tx.Abort(false);
                    if (observation is not null)
                        await RecordSchedulesAsync(policy, archive, observation.Runs, tx.Session, token);
                    var released = await policies.Find(
                            tx.Session,
                            BackupsFilters<BackupPolicy>.ById(policy.TenantId, policy.Id))
                        .SingleAsync(token);
                    await outbox(tx.Session, [BackupWorkRequests.Policy(released)], token);
                }

                return tx.Commit(true);
            },
            ct);
    }

    private async Task RecordSchedulesAsync(
        BackupPolicy policy,
        BackupArchive archive,
        IReadOnlyList<BackupObservedRun> runs,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        foreach (var run in runs)
        {
            var key = BackupOperationKeys.Scheduled(archive.Id, run.NativeUid);
            var filter = BackupDispatchFilters.Idempotency(archive.TenantId, key);
            var previous = await operations.Find(session, filter).SingleOrDefaultAsync(ct);
            ScheduledBackupExecution execution = run.State switch
            {
                BackupObservedRunState.Running => new RunningScheduledBackup(),
                BackupObservedRunState.Succeeded => new CompletedScheduledBackup(
                    run.BackupId ??
                    throw new InvalidOperationException("A completed native backup must report its identity.")),
                BackupObservedRunState.Failed => new FailedScheduledBackup(run.FailureCode ?? "backup-failed"),
                _ => throw new ArgumentOutOfRangeException(nameof(run)),
            };
            if (previous?.Work is ScheduledBackupWork recorded && recorded.Execution == execution)
                continue;
            var op = BackupOperation.ObserveScheduled(
                previous?.Id ?? ObjectId.GenerateNewId().ToString(),
                archive.TenantId,
                key,
                policy.Id,
                archive.Id,
                run.NativeUid,
                policy.Revision,
                execution,
                run.StartedAt.UtcDateTime,
                (run.FinishedAt ?? run.StartedAt).UtcDateTime,
                policy.Dispatch.Origin);
            await operations.ReplaceOneAsync(session, filter, op, new ReplaceOptions { IsUpsert = true }, ct);
            await commands.Changed(session, op, ct);
        }
    }
}
