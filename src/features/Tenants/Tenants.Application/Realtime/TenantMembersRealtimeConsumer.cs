using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Realtime;

public sealed class NotifyTenantMembersChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<TenantMembersChangedEvent>
{
    public Task Consume(ConsumeContext<TenantMembersChangedEvent> context) => publish(
        new RealtimeChange("tenant-members", context.Message.EventCore.TenantId),
        context.CancellationToken);
}
