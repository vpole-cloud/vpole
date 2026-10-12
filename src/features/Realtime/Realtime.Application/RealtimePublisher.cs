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
            topic.Scope == RealtimeScope.Platform && change.TenantId is not null ||
            topic.Scope == RealtimeScope.User && (change.TenantId is not null || string.IsNullOrWhiteSpace(change.ResourceId)))
            throw new InvalidOperationException("A realtime change must preserve its registered topic scope.");
        var recipients = await subscriptions.FindAsync(change.Topic, change.TenantId, cancellationToken);
        var authorized = new HashSet<string>(StringComparer.Ordinal);
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscription in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // User topics never permit a wildcard, even if a corrupt/stale registry entry does.
            if (topic.Scope == RealtimeScope.User && subscription.Request.ResourceId != change.ResourceId)
                continue;
            if (subscription.Request.ResourceId is { } resourceId && change.ResourceId is { } changedId && resourceId != changedId)
                continue;
            var ticket = await tickets.RetrieveAsync(subscription.SessionId);
            // Missing or expired transport state can recover through bootstrap. Only an explicit
            // logout marker revokes the user's independently authenticated workspace.
            if (subscription.ExpiresAtUtc <= clock.GetUtcNow() || ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow())
            {
                revoked.Add(subscription.ConnectionId);
                authorized.Remove(subscription.ConnectionId);
                await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], cancellationToken);
                if (await subscriptions.IsSessionRevokedAsync(subscription.SessionId, cancellationToken))
                    await hub.Clients.Client(subscription.ConnectionId).SessionRevoked();
                continue;
            }
            if (!await access.AllowsAsync(ticket.Principal, subscription.Request, cancellationToken))
            {
                revoked.Add(subscription.ConnectionId);
                authorized.Remove(subscription.ConnectionId);
                await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], cancellationToken);
                if (topic.Scope != RealtimeScope.User)
                    await hub.Clients.Client(subscription.ConnectionId).AccessRevoked(subscription.Request.TenantId);
                continue;
            }
            if (!revoked.Contains(subscription.ConnectionId)) authorized.Add(subscription.ConnectionId);
        }
        if (authorized.Count > 0) await hub.Clients.Clients(authorized.ToArray()).Changed(change);
    }
}
