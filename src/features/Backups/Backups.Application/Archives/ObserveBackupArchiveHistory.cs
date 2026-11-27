// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Archives;

public sealed class ObserveBackupArchiveHistory(
    IMongoCollection<BackupArchive> archives,
    BackupProviderCatalog providers,
    LoadRegisteredResource loadSource,
    ScheduledBackupHistory scheduled,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    ILogger<ObserveBackupArchiveHistory> logger
)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    public async Task<bool> ExecuteAsync(
        string tenantId,
        string archiveId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var candidate = await archives
            .Find(BackupsFilters<BackupArchive>.ById(tenantId, archiveId) & BackupHistoryFilters.Due(now))
            .SingleOrDefaultAsync(ct);
        if (candidate is null)
            return false;
        var lockId = ObjectId.GenerateNewId().ToString();
        var claimed = candidate with { History = candidate.History.Claim(new(lockId, now + Lease)) };
        if ((await archives.UpdateOneAsync(
                BackupHistoryFilters.AtRevision(candidate),
                Builders<BackupArchive>.Update.Set(a => a.History, claimed.History),
                cancellationToken: ct)).ModifiedCount != 1)
            return false;
        await BackupAttemptDiagnostics.RunAsync(
            logger,
            claimed.History.ExecutionOrigin,
            tenantId,
            claimed.PolicyId,
            archiveId,
            () => ExecuteClaimedAsync(claimed, lockId, ct),
            ct);
        return true;
    }

    private async Task ExecuteClaimedAsync(
        BackupArchive archive,
        string lockId,
        CancellationToken ct)
    {
        var monitoring = (MonitoringBackupHistory)archive.History;
        var provider = providers.Find(archive.ResourceType);
        var source = await loadSource(
            new(archive.TenantId, ResourceTypeId.Create(archive.ResourceType), archive.ResourceId),
            ct);
        var attempt = await ObserveAsync();

        async Task<BackupHistoryAttempt> ObserveAsync()
        {
            if (source is null)
                return new RemovedBackupHistorySource();
            if (provider is null)
                return new UnsupportedBackupHistoryProvider();
            var context = BackupArchiveAccess.Context(archive);
            return monitoring.Mode switch
            {
                PreparingBackupHistoryDrain prepare => new BackupHistorySuspensionAttempt(
                    await provider.SuspendArchiveAsync(new(archive.PolicyId, context, prepare.PolicyRevision), ct)),
                LiveBackupHistory or DrainingBackupHistory => monitoring.Checkpoint.Position switch
                {
                    FinishingBackupHistoryPosition finish => new FinalizeBackupHistoryBackfill(finish.Stream),
                    InitialBackupHistoryPosition or PagedBackupHistoryPosition or FollowingBackupHistoryPosition => new
                        BackupHistoryReadAttempt(
                            await provider.ReadHistoryAsync(
                                new(
                                    archive.PolicyId,
                                    context,
                                    Mode(monitoring.Mode),
                                    Position(monitoring.Checkpoint.Position)),
                                ct)),
                },
            };
        }

        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                var filter = BackupHistoryFilters.Claimed(archive, lockId, now);
                if (!await archives.Find(tx.Session, filter).AnyAsync(token))
                    return tx.Abort(false);
                var next = await AdvanceAsync(attempt, archive, monitoring, now, tx.Session, token);
                await archives.UpdateOneAsync(
                    tx.Session,
                    filter,
                    Builders<BackupArchive>.Update.Set(a => a.History, next),
                    cancellationToken: token);
                if (next is MonitoringBackupHistory)
                    await outbox(tx.Session, [BackupWorkRequests.History(archive with { History = next })], token);
                return tx.Commit(true);
            },
            ct);
    }

    private static BackupHistoryStart Start(MonitoringBackupHistory history) => new(
        ObjectId.GenerateNewId().ToString(),
        history.ExecutionOrigin);

    private static NativeBackupHistoryMode Mode(BackupHistoryMode mode) => mode switch
    {
        LiveBackupHistory => new LiveNativeBackupHistory(),
        DrainingBackupHistory drain => new SuspendedNativeBackupHistory(
            new(drain.Suspension.PolicyId, drain.Suspension.PolicyRevision, drain.Suspension.PublisherId)),
        PreparingBackupHistoryDrain => throw new InvalidOperationException(
            "The schedule must be suspended before listing."),
    };

    private static NativeBackupHistoryPosition Position(BackupHistoryPosition position) => position switch
    {
        InitialBackupHistoryPosition => new BeginNativeBackupHistory(),
        PagedBackupHistoryPosition page => new PageNativeBackupHistory(
            new(page.Stream.PublisherId, page.Stream.Version),
            page.Continuation),
        FollowingBackupHistoryPosition follow => new FollowNativeBackupHistory(
            new(follow.Stream.PublisherId, follow.Stream.Version)),
        FinishingBackupHistoryPosition => throw new InvalidOperationException(
            "Backfill finalization does not read Kubernetes."),
    };

    private static BackupHistoryPosition FromNative(NativeBackupHistoryPosition position) => position switch
    {
        PageNativeBackupHistory page => new PagedBackupHistoryPosition(
            new(page.Stream.PublisherId, page.Stream.Version),
            page.Continuation),
        FollowNativeBackupHistory follow => new FollowingBackupHistoryPosition(
            new(follow.Stream.PublisherId, follow.Stream.Version)),
        BeginNativeBackupHistory => throw new InvalidOperationException(
            "An accepted native batch must report its checkpoint."),
    };

    private async Task<BackupArchiveHistory> AdvanceAsync(
        BackupHistoryAttempt attempt,
        BackupArchive archive,
        MonitoringBackupHistory monitoring,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        return attempt switch
        {
            RemovedBackupHistorySource => await RemovedSourceAsync(),
            UnsupportedBackupHistoryProvider => monitoring.Retry(now, Retry, "unsupported-provider"),
            FinalizeBackupHistoryBackfill finish => await FinalizeAsync(finish.Stream),
            BackupHistorySuspensionAttempt suspension => suspension.Outcome switch
            {
                BackupArchiveSuspended parked => monitoring.RequestDrain(
                    new(parked.Suspension.PolicyId, parked.Suspension.PolicyRevision, parked.Suspension.PublisherId),
                    Start(monitoring),
                    now),
                BackupSourceUnavailable => RetryWith("backup-source-unavailable"),
                BackupNativeOwnershipConflict => RetryWith("backup-resource-ownership-conflict"),
                BackupNativeIdentityUnavailable => RetryWith("backup-native-identity-missing"),
                BackupNativeWriteConflict => RetryWith("backup-native-write-conflict"),
                BackupPolicyRevisionUnreadable => RetryWith("backup-policy-revision-unreadable"),
                BackupPolicySuperseded => RetryWith("backup-policy-superseded"),
            },
            BackupHistoryReadAttempt read => read.Outcome switch
            {
                BackupHistoryBatch batch => await AcceptAsync(batch),
                BackupSourceUnavailable => RetryWith("backup-source-unavailable"),
                BackupHistoryRestartRequired => monitoring.Restart(Start(monitoring), now),
                BackupHistorySuspensionChanged => monitoring.RecheckDrain(now),
                BackupHistoryPending => RetryWith("backup-schedule-not-created"),
                BackupHistoryUnreadable => RetryWith("backup-history-unreadable"),
                BackupHistoryDeadlineReached => RetryWith("backup-history-deadline-reached"),
                BackupNativeOwnershipConflict => RetryWith("backup-resource-ownership-conflict"),
                BackupNativeIdentityUnavailable => RetryWith("backup-native-identity-missing"),
            },
        };

        async Task<BackupArchiveHistory> FinalizeAsync(BackupHistoryStream stream)
        {
            var completed = await scheduled.FinalizeBackfillAsync(archive, monitoring, session, ct);
            BackupHistoryPosition position = completed
                ? new FollowingBackupHistoryPosition(stream)
                : new FinishingBackupHistoryPosition(stream);
            return monitoring.Accept(position, [], now, TimeSpan.Zero);
        }

        async Task<BackupArchiveHistory> RemovedSourceAsync() =>
            await scheduled.InterruptRemovedSourceAsync(archive, session, ct)
                ? monitoring.Stop("source-removed")
                : monitoring.Retry(now, TimeSpan.Zero, "source-removed");

        BackupArchiveHistory RetryWith(string reason) => monitoring.Retry(now, Retry, reason);

        async Task<BackupArchiveHistory> AcceptAsync(BackupHistoryBatch batch)
        {
            var synchronized = await scheduled.RecordBatchAsync(archive, monitoring, batch, session, ct);
            var position = FromNative(batch.Position);
            if (!synchronized && position is FollowingBackupHistoryPosition following)
                position = new FinishingBackupHistoryPosition(following.Stream);
            return monitoring.Accept(
                position,
                batch.Activities.Select(a => new BackupNativeActivity(a.NativeUid, a.InUse)).ToArray(),
                now,
                position is PagedBackupHistoryPosition or FinishingBackupHistoryPosition
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(1));
        }
    }
}

internal closed record BackupHistoryAttempt;

internal sealed record RemovedBackupHistorySource : BackupHistoryAttempt;

internal sealed record UnsupportedBackupHistoryProvider : BackupHistoryAttempt;

internal sealed record FinalizeBackupHistoryBackfill(
    BackupHistoryStream Stream
) : BackupHistoryAttempt;

internal sealed record BackupHistoryReadAttempt(
    ReadBackupHistoryOutcome Outcome
) : BackupHistoryAttempt;

internal sealed record BackupHistorySuspensionAttempt(
    SuspendBackupArchiveOutcome Outcome
) : BackupHistoryAttempt;
