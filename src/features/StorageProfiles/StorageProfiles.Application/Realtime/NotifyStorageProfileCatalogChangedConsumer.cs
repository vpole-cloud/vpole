using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Events;

namespace Pole.Features.StorageProfiles.Application.Realtime;

public sealed class NotifyStorageProfileCatalogChangedConsumer(
    PublishRealtimeChange publish,
    InvalidateRealtimeTenants invalidateTenants) : IConsumer<StorageProfileCatalogChangedEvent>
{
    public async Task HandleAsync(StorageProfileCatalogChangedEvent message, CancellationToken ct = default)
    {
        await publish(new RealtimeChange("storage-profiles", ResourceId: message.StorageProfileId,
            Revision: message.Revision), ct);
        // Eligibility is public tenant metadata. Never forward catalog IDs or configuration.
        await invalidateTenants("storage-eligibility", ct);
    }

    public Task Consume(ConsumeContext<StorageProfileCatalogChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);
}
