using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Common;

internal static class TenantAccessEventExtensions
{
    public static OutboxWrite MembershipChanged(string tenantId, string userId) => OutboxWrite.Create(
        new TenantMembershipChangedEvent(userId) { EventCore = EventCore.Create(tenantId) });
}
