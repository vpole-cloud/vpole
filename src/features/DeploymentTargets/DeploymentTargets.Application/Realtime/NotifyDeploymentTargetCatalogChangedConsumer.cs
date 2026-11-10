using MassTransit;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Realtime;

public sealed class NotifyDeploymentTargetCatalogChangedConsumer(
    PublishRealtimeChange publish,
    InvalidateRealtimeTenants invalidateTenants
) : IConsumer<DeploymentTargetCatalogChangedEvent>
{
    public Task Consume(ConsumeContext<DeploymentTargetCatalogChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(DeploymentTargetCatalogChangedEvent message, CancellationToken ct = default)
    {
        await publish(
            new RealtimeChange(
                "deployment-targets",
                ResourceId: message.DeploymentTargetId,
                Revision: message.Revision),
            ct);
        // A target's admission, health or credentials affect all three public choice lists.
        // Public hints contain only their authorized tenant, never private catalog metadata.
        await invalidateTenants("deployment-target-eligibility", ct);
        await invalidateTenants("compute-eligibility", ct);
        await invalidateTenants("storage-eligibility", ct);
    }
}
