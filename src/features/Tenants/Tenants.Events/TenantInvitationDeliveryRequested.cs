using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Tenants.Events;

[EntityName("pole.tenants/v1/invitation-delivery-requested")]
public sealed record TenantInvitationDeliveryRequested(
    string InvitationId,
    long Revision,
    EventCore EventCore
) : IPoleEvent;
