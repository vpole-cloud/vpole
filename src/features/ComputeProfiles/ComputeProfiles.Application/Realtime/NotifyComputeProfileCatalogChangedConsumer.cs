using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ComputeProfiles.Events;

namespace Pole.Features.ComputeProfiles.Application.Realtime;

public sealed class NotifyComputeProfileCatalogChangedConsumer(
    PublishRealtimeChange publish,
    InvalidateRealtimeTenants invalidateTenants) : IConsumer<ComputeProfileCatalogChangedEvent>
{
    public async Task HandleAsync(ComputeProfileCatalogChangedEvent message, CancellationToken ct = default)
    {
        await publish(new RealtimeChange("compute-profiles", ResourceId: message.ComputeProfileId,
            Revision: message.Revision), ct);
        // Eligibility is public tenant metadata. Never forward catalog IDs or configuration.
        await invalidateTenants("compute-eligibility", ct);
    }

    public Task Consume(ConsumeContext<ComputeProfileCatalogChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);
}
