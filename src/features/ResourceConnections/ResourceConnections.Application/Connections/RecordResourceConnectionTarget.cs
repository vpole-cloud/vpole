using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Common;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Connections;

public delegate Task RecordResourceConnectionApply(
    string tenantId,
    string connectionId,
    string targetId,
    long expectedGeneration,
    string? failureReason,
    CancellationToken cancellationToken = default);

public delegate Task RecordResourceConnectionCleanup(
    string tenantId,
    string connectionId,
    ConnectionPolicyLocation location,
    long expectedGeneration,
    string? failureReason,
    CancellationToken cancellationToken = default);

public delegate Task RecordResourceConnectionReverificationFailure(
    string tenantId,
    string connectionId,
    string targetId,
    long expectedGeneration,
    long expectedRevision,
    string reason,
    CancellationToken cancellationToken = default);

public delegate Task RecordResourceConnectionCleanupVerificationFailure(
    string tenantId,
    string connectionId,
    ConnectionPolicyLocation location,
    long expectedGeneration,
    long expectedRevision,
    string reason,
    CancellationToken cancellationToken = default);

public delegate Task PurgeResourceConnectionIfComplete(
    ResourceConnection connection,
    CancellationToken cancellationToken = default);

internal sealed class MongoRecordResourceConnectionTarget(
    IMongoCollection<ResourceConnection> connections,
    PurgeResourceConnectionIfComplete purgeIfComplete,
    TimeProvider timeProvider,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    IGitLifecycleCleanupProofRecorder cleanupProofs)
{
    public async Task RecordReverificationFailureAsync(
        string tenantId,
        string connectionId,
        string targetId,
        long expectedGeneration,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await TryRecordAsync(tenantId,
            connectionId,
            expectedRevision,
            current => ApplyChange(current.RecordVerificationFailure(
                targetId,
                expectedGeneration,
                reason,
                timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken);
    }

    public async Task RecordCleanupVerificationFailureAsync(
        string tenantId,
        string connectionId,
        ConnectionPolicyLocation location,
        long expectedGeneration,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await TryRecordAsync(tenantId,
            connectionId,
            expectedRevision,
            current => CleanupChange(current.RecordCleanupVerificationFailure(
                location,
                expectedGeneration,
                reason,
                timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken);
    }

    public Task RecordApplyAsync(
        string tenantId,
        string connectionId,
        string targetId,
        long expectedGeneration,
        string? failureReason,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            tenantId,
            connectionId,
            current => ApplyChange(failureReason is null
                ? current.RecordApplied(
                    targetId,
                    expectedGeneration,
                    timeProvider.GetUtcNow().UtcDateTime)
                : current.RecordApplyFailure(
                    targetId,
                    expectedGeneration,
                    failureReason,
                    timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken);

    public Task RecordCleanupAsync(
        string tenantId,
        string connectionId,
        ConnectionPolicyLocation location,
        long expectedGeneration,
        string? failureReason,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            tenantId,
            connectionId,
            current => CleanupChange(failureReason is null
                ? current.RecordCleanupCompleted(
                    location,
                    expectedGeneration,
                    timeProvider.GetUtcNow().UtcDateTime)
                : current.RecordCleanupFailure(
                    location,
                    expectedGeneration,
                    failureReason,
                    timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken);

    private static ResourceConnection? ApplyChange(ConnectionApplyOutcome outcome) => outcome switch
    {
        ConnectionApplyRecorded recorded => recorded.Connection,
        StaleConnectionApply => null,
        ConnectionNoLongerActive => null,
        ConnectionApplyAlreadyApplied => null,
        UnknownConnectionApplyTarget => null
    };

    private static ResourceConnection? CleanupChange(ConnectionCleanupOutcome outcome) => outcome switch
    {
        ConnectionCleanupRecorded recorded => recorded.Connection,
        StaleConnectionCleanup => null,
        CurrentConnectionPolicyLocation => null,
        ConnectionCleanupAlreadyCompleted => null,
        UnknownConnectionCleanupTarget => null
    };

    private async Task RecordAsync(
        string tenantId,
        string connectionId,
        Func<ResourceConnection, ResourceConnection?> change,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var outcome = await TryRecordAsync(tenantId, connectionId, null, change, cancellationToken);
            if (outcome.Value is ResourceConnectionTargetRecorded recorded)
            {
                await purgeIfComplete(recorded.Connection, cancellationToken);
                return;
            }

            if (outcome.Value is ResourceConnectionTargetUnchanged unchanged)
            {
                if (unchanged.Connection is not null)
                    await purgeIfComplete(unchanged.Connection, cancellationToken);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Connection '{connectionId}' changed repeatedly while recording target status.");
    }

    private Task<ResourceConnectionTargetOutcome> TryRecordAsync(string tenantId,
        string connectionId,
        long? expectedRevision,
        Func<ResourceConnection, ResourceConnection?> change,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ResourceConnectionTargetOutcome>(async (tx, token) =>
            {
                var filter = expectedRevision is long revision
                    ? ResourceConnectionFilters.AtRevision(tenantId, connectionId, revision)
                    : ResourceConnectionFilters.ByTenantAndId(tenantId, connectionId);
                var current = await connections.Find(tx.Session, filter).FirstOrDefaultAsync(token);
                var changed = current is null ? null : change(current);
                if (changed is null)
                    return tx.Commit(new ResourceConnectionTargetUnchanged(current));
                var updated = await connections.ReplaceOneAsync(tx.Session,
                    ResourceConnectionFilters.AtRevision(tenantId, connectionId, current!.Revision),
                    changed,
                    cancellationToken: token);
                if (updated.ModifiedCount != 1)
                    return tx.Abort(new ResourceConnectionTargetConflict());
                if (current.Lifecycle is DeletingResourceConnection &&
                    changed.Lifecycle is DeletingResourceConnection && current.Generation == changed.Generation)
                    await cleanupProofs.RecordAsync(new(current.TenantId,
                        new GitDeclaredCleanupResource(DeclarativeResourceConnectionProvider.ResourceKind, current.Id),
                        $"connection-status/{current.Id}/{current.Generation}/{changed.Revision}",
                        [new(ResourceConnectionPlanningVersion.RevisionDocumentId(current.Id), current.Revision,
                            changed.Revision)]), tx.Session, token);

                await outbox(tx.Session, [OutboxWrite.Create(changed.ToChangedEvent())], token);
                return tx.Commit(new ResourceConnectionTargetRecorded(changed));
            },
            ct);
}

internal sealed record ResourceConnectionTargetRecorded(ResourceConnection Connection);

internal sealed record ResourceConnectionTargetUnchanged(ResourceConnection? Connection);

internal sealed record ResourceConnectionTargetConflict;

internal union ResourceConnectionTargetOutcome(
    ResourceConnectionTargetRecorded,
    ResourceConnectionTargetUnchanged,
    ResourceConnectionTargetConflict);
