using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Progress
    = Pole.Features.Backups.Contracts.BackupProviderResult<Pole.Features.Backups.Contracts.BackupProviderProgress>;

namespace Pole.Features.Backups.Application.Execution;

public sealed class BackupDispatcher(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupOperation> operations,
    BackupProviders providers,
    BackupArchiveAccess access,
    BackupOperations commands,
    BackupConfigurationWriter configuration,
    MongoRunTransaction transactions,
    LoadRegisteredResource loadSource,
    TimeProvider clock
)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public async Task<bool> PolicyAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = ObjectId.GenerateNewId().ToString();
        var candidate = BackupDispatchFilters.PoliciesDue(now);
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
                new(policy.Id, BackupArchiveAccess.Context(archive, "", "", ""), null, policy.Revision),
                attempt);
        else
        {
            var runtime = await access.RuntimeAsync(archive, attempt);
            effect = runtime is BackupProviderResult<BackupArchiveContext>.Success resolved
                ? await provider.ApplyScheduleAsync(
                    new(
                        policy.Id,
                        resolved.Value,
                        new(policy.Schedule, policy.Suspended, policy.RetentionDays),
                        policy.Revision),
                    attempt)
                : new Progress.Failure("workload-credential-unavailable", true);
            if (effect is Progress.Success)
            {
                var observed = await provider.ObserveAsync(BackupArchiveAccess.Context(archive, "", "", ""), attempt);
                if (observed is BackupProviderResult<BackupRuntimeObservation>.Success observedState)
                    observation = observedState.Value;
                else
                    effect = new Progress.Failure(
                        ((BackupProviderResult<BackupRuntimeObservation>.Failure)observed).Code,
                        true);
            }
        }

        var latest = source is not null && provider is not null
            ? await provider.DescribeAsync(new(policy.TenantId, policy.ResourceType, policy.ResourceId), attempt)
            : null;

        var succeeded = effect is Progress.Success { Value.Phase: BackupProviderPhase.Succeeded };
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var guard = BackupDispatchFilters.ClaimedPolicy(policy, lockId, clock.GetUtcNow().UtcDateTime);
                if (!await policies.Find(tx.Session, guard).AnyAsync(token))
                    return tx.Abort(false);
                if (latest is BackupProviderResult<BackupServiceSnapshot>.Success { Value: var service } &&
                    service.Revision > archive.SourceRevision)
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
                        await configuration.RetireArchive(policy, tx.Session, token, sourceRemoved: source is null);
                        await configuration.Changed(tx.Session, policy, "BackupPolicy", policy.Revision + 1, token);
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
                }

                return tx.Commit(true);
            },
            ct);
        return true;
    }

    public async Task<bool> OperationAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = ObjectId.GenerateNewId().ToString();
        var work = BackupDispatchFilters.OperationsDue(now);
        var operation = await operations.FindOneAndUpdateAsync(
            work,
            Builders<BackupOperation>.Update.Set(o => o.LeaseId, lockId).Set(o => o.LeaseUntil, now + Lease),
            new FindOneAndUpdateOptions<BackupOperation>
                { Sort = Builders<BackupOperation>.Sort.Ascending(o => o.NextAttemptAt) },
            ct);
        if (operation is null)
            return false;
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(operation.TenantId, operation.ArchiveId))
            .SingleAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Lease / 2);
        var provider = providers.Find(archive.ResourceType);
        var effect = provider is null
            ? new Progress.Failure("unsupported-provider", false)
            : await ExecuteAsync(operation, archive, provider, deadline.Token);
        var nextState = effect switch
        {
            Progress.Success { Value.Phase: BackupProviderPhase.Succeeded } => BackupOperationState.Succeeded,
            Progress.Failure { Retryable: false } => BackupOperationState.Failed,
            Progress.Failure { Retryable: true } => operation.State,
            _ => BackupOperationState.Running,
        };
        var updatedAt = clock.GetUtcNow().UtcDateTime;
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var guard = BackupDispatchFilters.ClaimedOperation(operation, lockId, clock.GetUtcNow().UtcDateTime);
                var current = await operations.Find(tx.Session, guard).SingleOrDefaultAsync(token);
                if (current is null)
                    return tx.Abort(false);
                // The provider may have committed the creation receipt after this lease was claimed.
                var updated = current.Advance(
                    nextState,
                    (effect as Progress.Failure)?.Code,
                    (effect as Progress.Success)?.Value.ProviderBackupId,
                    updatedAt,
                    effect is Progress.Failure ? Retry : Poll);
                if ((await operations.ReplaceOneAsync(tx.Session, guard, updated, cancellationToken: token))
                    .MatchedCount != 1)
                    return tx.Abort(false);
                if (updated.State != current.State || updated.FailureCode != current.FailureCode)
                    await commands.Changed(tx.Session, updated, token);
                return tx.Commit(true);
            },
            ct);
        return true;
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
            var key = $"scheduled:{archive.Id}:{run.NativeUid}";
            var filter = BackupDispatchFilters.Idempotency(archive.TenantId, key);
            var previous = await operations.Find(session, filter).SingleOrDefaultAsync(ct);
            var state = run.State switch
            {
                BackupObservedRunState.Running => BackupOperationState.Running,
                BackupObservedRunState.Succeeded => BackupOperationState.Succeeded,
                BackupObservedRunState.Failed => BackupOperationState.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(run))
            };
            if (previous?.State == state && previous.FailureCode == run.FailureCode &&
                previous.BackupId == run.BackupId)
                continue;
            var op = new BackupOperation(
                previous?.Id ?? ObjectId.GenerateNewId().ToString(),
                archive.TenantId,
                key,
                BackupOperationKind.ScheduledBackup,
                state,
                policy.Id,
                archive.Id,
                run.BackupId,
                null,
                null,
                null,
                run.FailureCode,
                run.StartedAt.UtcDateTime,
                (run.FinishedAt ?? run.StartedAt).UtcDateTime,
                run.StartedAt.UtcDateTime,
                null,
                null,
                policy.Revision,
                null,
                false);
            await operations.ReplaceOneAsync(session, filter, op, new ReplaceOptions { IsUpsert = true }, ct);
            await commands.Changed(session, op, ct);
        }
    }

    private async Task<Progress> ExecuteAsync(
        BackupOperation operation,
        BackupArchive archive,
        IBackupProvider provider,
        CancellationToken ct)
    {
        if (operation.Kind == BackupOperationKind.Backup && !archive.Active)
            return new Progress.Failure("archive-retired", false);
        var targetCreated = operation.Kind == BackupOperationKind.Restore &&
            (operation.RestoreTargetCreated || operation.State == BackupOperationState.Running);
        if (targetCreated && await loadSource(
                new(operation.TenantId, ResourceTypeId.Create(archive.ResourceType), operation.NewResourceId!),
                ct) is null)
            return new Progress.Failure("restore-resource-deleted", false);
        var runtime = await access.RuntimeAsync(archive, ct);
        if (runtime is not BackupProviderResult<BackupArchiveContext>.Success success)
            return new Progress.Failure("workload-credential-unavailable", true);
        if (operation.Kind == BackupOperationKind.Backup)
            return await provider.RunAsync(new(operation.Id, success.Value), ct);
        if (!targetCreated)
        {
            var invalid = await commands.VerifyTargetAsync(archive, operation.BackupId, operation.TargetTime, ct);
            if (invalid is not null)
                return new Progress.Failure(invalid, invalid != "target-unavailable");
        }

        return await provider.RestoreAsync(
            new(
                operation.Id,
                success.Value,
                operation.NewResourceId!,
                operation.Name!,
                operation.BackupId,
                operation.TargetTime,
                operation.RestoreStorageGiB),
            ct);
    }
}
