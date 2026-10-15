using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Realtime;

public sealed class NotifyTenantAccessChangedConsumer(PublishRealtimeChange publish)
    : IConsumer<TenantMembershipChangedEvent>
{
    public Task HandleAsync(TenantMembershipChangedEvent message, CancellationToken cancellationToken = default) =>
        publish(new("tenant-access", ResourceId: message.UserId), cancellationToken);

    public Task Consume(ConsumeContext<TenantMembershipChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);
}
