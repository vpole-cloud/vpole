using Hangfire;
using Microsoft.Extensions.Logging;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Application.Reconciliation;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Jobs;

public sealed class ResourceConnectionRecoveryJob(
    LoadResourceConnectionRecoveryPage loadPage,
    LoadResourceConnectionTombstoneRecoveryPage loadTombstones,
    ResourceConnectionReconciler reconciler,
    IResourceConnectionPolicyTarget policyTarget,
    ILogger<ResourceConnectionRecoveryJob> logger)
{
    private const int PageSize = 100;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        string? afterId = null;
        while (true)
        {
            var page = await loadPage(afterId, PageSize, cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var connection in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NeedsReconciliation(connection))
                    continue;
                await reconciler.ReconcileAsync(
                    connection.TenantId,
                    connection.Id,
                    connection.Generation,
                    cancellationToken);
            }
            afterId = page[^1].Id;
        }
        afterId = null;
        while (true)
        {
            var page = await loadTombstones(afterId, PageSize, cancellationToken);
            if (page.Count == 0)
                return;
            foreach (var tombstone in page)
            {
                foreach (var snapshot in tombstone.Targets)
                {
                    var outcome = await policyTarget.DeleteTombstoneAsync(
                        tombstone,
                        snapshot,
                        cancellationToken);
                    if (outcome is ResourceConnectionPolicyFailed failed)
                    {
                        logger.LogWarning(
                            "Connection tombstone {ConnectionId} cleanup retry failed for target {TargetId}: {Reason}",
                            tombstone.Id,
                            snapshot.TargetId,
                            failed.Reason);
                    }
                }
            }
            afterId = page[^1].Id;
        }
    }

    private static bool NeedsReconciliation(ResourceConnection connection) =>
        connection.Lifecycle switch
        {
            ActiveResourceConnection => true,
            DeletingResourceConnection => true,
            _ => false
        };
}
