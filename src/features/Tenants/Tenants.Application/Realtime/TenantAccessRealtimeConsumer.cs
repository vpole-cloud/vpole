using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Realtime;

public sealed class NotifyTenantAccessChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<TenantMembershipChangedEvent>
{
    public Task Consume(ConsumeContext<TenantMembershipChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(TenantMembershipChangedEvent message, CancellationToken cancellationToken = default)
    {
        await publish(new RealtimeChange("tenant-access", ResourceId: message.UserId), cancellationToken);
        await publish(new RealtimeChange("tenant-members", message.EventCore.TenantId), cancellationToken);
    }
}
