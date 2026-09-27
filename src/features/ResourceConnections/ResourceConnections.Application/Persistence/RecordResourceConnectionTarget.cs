using MongoDB.Driver;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Persistence;

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
    TimeProvider timeProvider)
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
        var current = await connections.Find(ResourceConnectionFilters.AtRevision(
                tenantId,
                connectionId,
                expectedRevision))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return;
        var outcome = current.RecordVerificationFailure(
            targetId,
            expectedGeneration,
            reason,
            timeProvider.GetUtcNow().UtcDateTime);
        if (outcome is not ConnectionApplyRecorded recorded)
            return;
        await connections.ReplaceOneAsync(
            ResourceConnectionFilters.AtRevision(tenantId, connectionId, expectedRevision),
            recorded.Connection,
            cancellationToken: cancellationToken);
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
        var current = await connections.Find(ResourceConnectionFilters.AtRevision(
                tenantId,
                connectionId,
                expectedRevision))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return;
        var outcome = current.RecordCleanupVerificationFailure(
            location,
            expectedGeneration,
            reason,
            timeProvider.GetUtcNow().UtcDateTime);
        if (outcome is not ConnectionCleanupRecorded recorded)
            return;
        await connections.ReplaceOneAsync(
            ResourceConnectionFilters.AtRevision(tenantId, connectionId, expectedRevision),
            recorded.Connection,
            cancellationToken: cancellationToken);
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
            current => failureReason is null
                ? current.RecordApplied(
                    targetId,
                    expectedGeneration,
                    timeProvider.GetUtcNow().UtcDateTime).Value as ConnectionApplyRecorded
                : current.RecordApplyFailure(
                    targetId,
                    expectedGeneration,
                    failureReason,
                    timeProvider.GetUtcNow().UtcDateTime).Value as ConnectionApplyRecorded,
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
            current => failureReason is null
                ? current.RecordCleanupCompleted(
                    location,
                    expectedGeneration,
                    timeProvider.GetUtcNow().UtcDateTime).Value as ConnectionCleanupRecorded
                : current.RecordCleanupFailure(
                    location,
                    expectedGeneration,
                    failureReason,
                    timeProvider.GetUtcNow().UtcDateTime).Value as ConnectionCleanupRecorded,
            cancellationToken);

    private async Task RecordAsync<TRecorded>(
        string tenantId,
        string connectionId,
        Func<ResourceConnection, TRecorded?> change,
        CancellationToken cancellationToken)
        where TRecorded : class
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await connections.Find(
                    ResourceConnectionFilters.ByTenantAndId(tenantId, connectionId))
                .FirstOrDefaultAsync(cancellationToken);
            if (current is null)
                return;
            var outcome = change(current);
            var changed = outcome switch
            {
                ConnectionApplyRecorded applied => applied.Connection,
                ConnectionCleanupRecorded cleaned => cleaned.Connection,
                _ => null
            };
            if (changed is null)
            {
                await purgeIfComplete(current, cancellationToken);
                return;
            }
            var replaced = await connections.ReplaceOneAsync(
                ResourceConnectionFilters.AtRevision(
                    tenantId,
                    connectionId,
                    current.Revision),
                changed,
                cancellationToken: cancellationToken);
            if (replaced.ModifiedCount != 1)
                continue;
            await purgeIfComplete(changed, cancellationToken);
            return;
        }
        throw new InvalidOperationException(
            $"Connection '{connectionId}' changed repeatedly while recording target status.");
    }

}
