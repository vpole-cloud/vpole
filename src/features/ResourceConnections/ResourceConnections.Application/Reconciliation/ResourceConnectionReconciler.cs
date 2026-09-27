using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Reconciliation;

public sealed class ResourceConnectionReconciler(
    LoadResourceConnection loadConnection,
    IResourceConnectionPolicyTarget policyTarget,
    RecordResourceConnectionApply recordApply,
    RecordResourceConnectionReverificationFailure recordReverificationFailure,
    RecordResourceConnectionCleanup recordCleanup,
    RecordResourceConnectionCleanupVerificationFailure recordCleanupVerificationFailure,
    PurgeResourceConnectionIfComplete purgeIfComplete)
{
    public async Task ReconcileAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        var connection = await loadConnection(tenantId, connectionId, cancellationToken);
        if (connection is null || connection.Generation != expectedGeneration)
            return;

        if (connection.Lifecycle is ActiveResourceConnection active)
        {
            foreach (var target in active.RetiringTargets)
            {
                await CleanupAsync(
                    connection,
                    target.Snapshot,
                    target.Status is CompletedConnectionCleanup,
                    cancellationToken);
            }
            foreach (var target in active.Targets)
            {
                await ApplyAsync(
                    connection,
                    target.Snapshot,
                    target.Status is AppliedConnectionApply,
                    cancellationToken);
            }
        }
        else if (connection.Lifecycle is DeletingResourceConnection deleting)
        {
            foreach (var target in deleting.Targets)
            {
                await CleanupAsync(
                    connection,
                    target.Snapshot,
                    target.Status is CompletedConnectionCleanup,
                    cancellationToken);
            }
            await purgeIfComplete(connection, cancellationToken);
        }
    }

    private async Task ApplyAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        bool wasApplied,
        CancellationToken cancellationToken)
    {
        var result = await policyTarget.ApplyAsync(connection, snapshot, cancellationToken);
        switch (result)
        {
            case ResourceConnectionPolicySucceeded:
                await recordApply(
                    connection.TenantId,
                    connection.Id,
                    snapshot.TargetId,
                    connection.Generation,
                    failureReason: null,
                    cancellationToken);
                break;
            case ResourceConnectionPolicyFailed failed:
                if (wasApplied)
                {
                    await recordReverificationFailure(
                        connection.TenantId,
                        connection.Id,
                        snapshot.TargetId,
                        connection.Generation,
                        connection.Revision,
                        failed.Reason.ToString(),
                        cancellationToken);
                }
                else
                {
                    await recordApply(
                        connection.TenantId,
                        connection.Id,
                        snapshot.TargetId,
                        connection.Generation,
                        failed.Reason.ToString(),
                        cancellationToken);
                }
                break;
            case ResourceConnectionPolicyDesiredStateChanged:
                break;
        }
    }

    private async Task CleanupAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        bool wasCompleted,
        CancellationToken cancellationToken)
    {
        var result = await policyTarget.DeleteAsync(connection, snapshot, cancellationToken);
        switch (result)
        {
            case ResourceConnectionPolicySucceeded:
                await recordCleanup(
                    connection.TenantId,
                    connection.Id,
                    snapshot.PolicyLocation,
                    connection.Generation,
                    failureReason: null,
                    cancellationToken);
                break;
            case ResourceConnectionPolicyFailed failed:
                if (wasCompleted)
                {
                    await recordCleanupVerificationFailure(
                        connection.TenantId,
                        connection.Id,
                        snapshot.PolicyLocation,
                        connection.Generation,
                        connection.Revision,
                        failed.Reason.ToString(),
                        cancellationToken);
                }
                else
                {
                    await recordCleanup(
                        connection.TenantId,
                        connection.Id,
                        snapshot.PolicyLocation,
                        connection.Generation,
                        failed.Reason.ToString(),
                        cancellationToken);
                }
                break;
            case ResourceConnectionPolicyDesiredStateChanged:
                break;
        }
    }
}
