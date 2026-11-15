using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Tenants.Events;

[EntityName("pole.tenants/v1/members-changed")]
public sealed record TenantMembersChangedEvent(
    EventCore EventCore
) : IPoleEvent;
