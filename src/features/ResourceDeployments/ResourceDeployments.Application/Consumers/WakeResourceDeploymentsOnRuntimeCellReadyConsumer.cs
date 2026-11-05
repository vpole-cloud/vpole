using MassTransit;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
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
        var woken = await wakeResourceDeployments(
            message.EventCore.TenantId,
            message.RuntimeCellId,
            settings.RecoveryBatchSize,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        metrics.RecordRuntimeCellWakeups(woken);
    }
}
