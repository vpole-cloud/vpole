using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public sealed class RealtimePublisher(
    IRealtimeSubscriptions subscriptions,
    IRealtimeTicketStore tickets,
    RealtimeAccess access,
    RealtimeTopicRegistry topics,
    IHubContext<RealtimeHub, IRealtimeClient> hub,
    TimeProvider clock)
{
    public async Task PublishAsync(RealtimeChange change, CancellationToken cancellationToken = default)
    {
        var topic = topics.Find(change.Topic);
        if (topic is null || !topics.IsEnabled(change.Topic)) return;
        if (topic.Scope == RealtimeScope.Tenant && change.TenantId is null ||
            topic.Scope == RealtimeScope.Platform && change.TenantId is not null)
            throw new InvalidOperationException("A realtime change must preserve its registered topic scope.");
        var recipients = await subscriptions.FindAsync(change.Topic, change.TenantId, cancellationToken);
        var authorized = new HashSet<string>(StringComparer.Ordinal);
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscription in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subscription.Request.ResourceId is { } resourceId && change.ResourceId is { } changedId && resourceId != changedId)
                continue;
            var ticket = await tickets.RetrieveAsync(subscription.SessionId);
            if (subscription.ExpiresAtUtc <= clock.GetUtcNow() ||
                ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow() ||
                !await access.AllowsAsync(ticket.Principal, subscription.Request, cancellationToken))
            {
                revoked.Add(subscription.ConnectionId);
                authorized.Remove(subscription.ConnectionId);
                await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], cancellationToken);
                await hub.Clients.Client(subscription.ConnectionId).AccessRevoked(subscription.Request.TenantId);
                continue;
            }
            if (!revoked.Contains(subscription.ConnectionId)) authorized.Add(subscription.ConnectionId);
        }
        if (authorized.Count > 0) await hub.Clients.Clients(authorized.ToArray()).Changed(change);
    }
}
