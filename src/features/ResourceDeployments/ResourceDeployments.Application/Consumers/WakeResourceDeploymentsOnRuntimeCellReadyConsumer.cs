using MassTransit;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceDeployments.Application.Consumers;

public sealed class WakeResourceDeploymentsOnRuntimeCellReadyConsumer(
    WakeResourceDeploymentsForRuntimeCell wakeResourceDeployments,
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
        var result = await wakeResourceDeployments(
            message.EventCore.TenantId,
            message.RuntimeCellId,
            settings.RecoveryBatchSize,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        if (result.TryGetValue(out var woken, out _))
            metrics.RecordRuntimeCellWakeups(woken);
    }
}
