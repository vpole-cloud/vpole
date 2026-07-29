using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Consumers;

public sealed class WakeResourceDeploymentsOnRuntimeCellReadyConsumer(
    IResourceDeploymentDbContext dbContext,
    ResourceDeploymentSettings settings,
    ResourceDeploymentMetrics metrics,
    TimeProvider timeProvider)
    : IConsumer<RuntimeCellReadyEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellReadyEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        RuntimeCellReadyEvent message,
        CancellationToken cancellationToken = default)
    {
        var waitingFilter = Builders<ResourceDeployment>.Filter.And(
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.TenantId,
                message.EventCore.TenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.RuntimeCellId,
                message.RuntimeCellId),
            Builders<ResourceDeployment>.Filter
                .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(
                    deployment => deployment.State));
        var waiting = await dbContext.ResourceDeployments
            .Find(waitingFilter)
            .SortBy(deployment => deployment.Id)
            .Limit(settings.RecoveryBatchSize)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
            return;

        var woken = 0L;
        var result = await dbContext.RunInTransactionAsync(
            async (session, ct) =>
            {
                var writes = new List<OutboxWrite>(waiting.Count);
                foreach (var deployment in waiting)
                {
                    var current = deployment.MarkRuntimeCellReady(
                        timeProvider.GetUtcNow().UtcDateTime);
                    var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
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
                    {
                        writes.Add(current.ToCurrentRequestWrite());
                        woken++;
                    }
                }

                await dbContext.StoreEventAsync(writes, session, ct);
                return Result.Success();
            },
            cancellationToken: cancellationToken);
        if (result is ISuccess)
            metrics.RecordRuntimeCellWakeups(woken);
    }
}
