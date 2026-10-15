using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Tenants.Events;

/// <summary>Discovery changed for this subject; roles and membership lists remain HTTP data.</summary>
[EntityName("pole.tenants/v1/membership-changed")]
public sealed record TenantMembershipChangedEvent(string UserId) : IPoleEvent
{
    public EventCore EventCore
    {
        get;
        init;
    } = EventCore.Create();
}
