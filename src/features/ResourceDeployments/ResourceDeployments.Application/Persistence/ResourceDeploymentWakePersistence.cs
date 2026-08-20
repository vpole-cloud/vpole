using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<Result<long>> WakeResourceDeploymentsForRuntimeCell(
    string tenantId,
    string runtimeCellId,
    int batchSize,
    DateTime readyAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class ResourceDeploymentWakePersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoTransaction<long> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<Result<long>> WakeAsync(
        string tenantId,
        string runtimeCellId,
        int batchSize,
        DateTime readyAtUtc,
        CancellationToken cancellationToken = default)
    {
        var waitingFilter = Builders<ResourceDeployment>.Filter.And(
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.TenantId,
                tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.RuntimeCellId,
                runtimeCellId),
            Builders<ResourceDeployment>.Filter
                .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(
                    deployment => deployment.State));
        var waiting = await resourceDeployments
            .Find(waitingFilter)
            .SortBy(deployment => deployment.Id)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
            return 0L;

        return await runMongoTransaction(async (session, ct) =>
        {
            var writes = new List<OutboxWrite>(waiting.Count);
            foreach (var deployment in waiting)
            {
                var current = deployment.MarkRuntimeCellReady(readyAtUtc);
                var replacement = await resourceDeployments.ReplaceOneAsync(
                    session,
                    Builders<ResourceDeployment>.Filter.And(
                        Builders<ResourceDeployment>.Filter.Eq(
                            item => item.Id,
                            deployment.Id),
                        Builders<ResourceDeployment>.Filter.Eq(
                            item => item.TenantId,
                            deployment.TenantId),
                        Builders<ResourceDeployment>.Filter.Eq(
                            item => item.Revision,
                            deployment.Revision),
                        Builders<ResourceDeployment>.Filter
                            .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(
                                item => item.State)),
                    current,
                    cancellationToken: ct);
                if (replacement.ModifiedCount == 1)
                    writes.Add(current.ToCurrentRequestWrite());
            }

            await storeOutboxWrites(session, writes, ct);
            return writes.Count;
        }, cancellationToken);
    }
}
