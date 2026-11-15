using MassTransit;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Consumers;

public sealed class DeliverTenantInvitationConsumer(
    DeliverTenantInvitation delivery
) : IConsumer<TenantInvitationDeliveryRequested>
{
    public Task Consume(ConsumeContext<TenantInvitationDeliveryRequested> context) =>
        delivery.InvokeAsync(context.Message, context.CancellationToken);
}
