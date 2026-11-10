using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public sealed class RealtimePublisher(
    IRealtimeSubscriptions subscriptions,
    IRealtimeTicketStore tickets,
    RealtimeAccess access,
    RealtimeTopicRegistry topics,
    IHubContext<RealtimeHub, IRealtimeClient> hub,
    TimeProvider clock,
    RealtimeMetrics? metrics = null
)
{
    public async Task InvalidateTenantsAsync(string topic, CancellationToken cancellationToken = default)
    {
        if (topics.Find(topic)?.Scope != RealtimeScope.Tenant)
            throw new InvalidOperationException("Public eligibility invalidation requires a registered tenant topic.");
        if (!topics.IsEnabled(topic))
            return;
        var recipients = await subscriptions.FindAllAsync(topic, cancellationToken);
        var tenants = recipients.Where(subscription => subscription.ExpiresAtUtc > clock.GetUtcNow())
            .Select(subscription => subscription.Request.TenantId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        // Publish rechecks every recipient's current ticket and tenant permission. The
        // platform mutation cannot choose recipients or copy privileged identifiers.
        foreach (var tenantId in tenants)
            await PublishAsync(new RealtimeChange(topic, tenantId), cancellationToken);
    }

    public async Task PublishAsync(RealtimeChange change, CancellationToken cancellationToken = default)
    {
        var topic = topics.Find(change.Topic);
        var started = Stopwatch.GetTimestamp();
        var outcome = RealtimeDeliveryOutcome.Failed;
        var recipientCount = 0;
        try
        {
            if (topic is null || !topics.IsEnabled(change.Topic))
            {
                outcome = RealtimeDeliveryOutcome.Unavailable;
                return;
            }

            if ((topic.Scope == RealtimeScope.Tenant && change.TenantId is null) ||
                (topic.Scope == RealtimeScope.Platform && change.TenantId is not null) ||
                (topic.Scope == RealtimeScope.User &&
                    (change.TenantId is not null || string.IsNullOrWhiteSpace(change.ResourceId))))
                throw new InvalidOperationException("A realtime change must preserve its registered topic scope.");
            var recipients = await subscriptions.FindAsync(change.Topic, change.TenantId, cancellationToken);
            var authorized = new HashSet<string>(StringComparer.Ordinal);
            var revoked = new HashSet<string>(StringComparer.Ordinal);
            var deadlines = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var identities = new Dictionary<string, ClaimsPrincipal>(StringComparer.Ordinal);
            foreach (var subscription in recipients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // User topics never permit a wildcard, even if a corrupt/stale registry entry does.
                if (topic.Scope == RealtimeScope.User && subscription.Request.ResourceId != change.ResourceId)
                    continue;
                if (subscription.Request.ResourceId is { } resourceId && change.ResourceId is { } changedId &&
                    resourceId != changedId)
                    continue;
                var ticket = await tickets.RetrieveAsync(subscription.SessionId);
                // Missing or expired transport state can recover through bootstrap. Only an explicit
                // logout marker revokes the user's independently authenticated workspace.
                if (subscription.ExpiresAtUtc <= clock.GetUtcNow() || ticket?.Properties.ExpiresUtc is not { } expiry ||
                    expiry <= clock.GetUtcNow() || ticket.Principal.Identity?.IsAuthenticated != true ||
                    string.IsNullOrWhiteSpace(ticket.Principal.FindFirst("sub")?.Value) ||
                    ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)?.Value != subscription.SessionId)
                {
                    revoked.Add(subscription.ConnectionId);
                    authorized.Remove(subscription.ConnectionId);
                    await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], cancellationToken);
                    if (await subscriptions.IsSessionRevokedAsync(subscription.SessionId, cancellationToken))
                        await hub.Clients.Client(subscription.ConnectionId).SessionRevoked();
                    continue;
                }

                var deadline = expiry < subscription.ExpiresAtUtc ? expiry : subscription.ExpiresAtUtc;
                var allowed = await access.AllowsAsync(ticket.Principal, subscription.Request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var live = await LiveTicketAsync(subscription, deadline, ticket.Principal, cancellationToken);
                if (live is null)
                {
                    revoked.Add(subscription.ConnectionId);
                    authorized.Remove(subscription.ConnectionId);
                    await DropSessionAsync(subscription, cancellationToken);
                    continue;
                }

                if (!allowed)
                {
                    revoked.Add(subscription.ConnectionId);
                    authorized.Remove(subscription.ConnectionId);
                    await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], cancellationToken);
                    if (topic.Scope != RealtimeScope.User)
                        await hub.Clients
                            .Client(subscription.ConnectionId)
                            .AccessRevoked(subscription.Request.TenantId);
                    continue;
                }

                if (!revoked.Contains(subscription.ConnectionId))
                {
                    authorized.Add(subscription.ConnectionId);
                    deadlines[subscription.Id] = Earlier(deadline, live.Properties.ExpiresUtc!.Value);
                    identities[subscription.Id] = ticket.Principal;
                }
            }

            // Membership work for a later recipient must not extend an earlier recipient's
            // authorization. Dispatch each connection only after its final awaited checks.
            foreach (var connectionId in authorized)
            {
                var candidates = recipients
                    .Where(value => value.ConnectionId == connectionId && deadlines.ContainsKey(value.Id))
                    .ToArray();
                var valid = true;
                ClaimsPrincipal? authority = null;
                foreach (var subscription in candidates)
                {
                    var before = await LiveTicketAsync(
                        subscription,
                        deadlines[subscription.Id],
                        identities[subscription.Id],
                        cancellationToken);
                    if (before is null)
                    {
                        await DropSessionAsync(subscription, cancellationToken);
                        valid = false;
                        break;
                    }

                    deadlines[subscription.Id] = Earlier(
                        deadlines[subscription.Id],
                        before.Properties.ExpiresUtc!.Value);
                    var allowed = await access.AllowsAsync(before.Principal, subscription.Request, cancellationToken);
                    var after = await LiveTicketAsync(
                        subscription,
                        deadlines[subscription.Id],
                        identities[subscription.Id],
                        cancellationToken);
                    if (after is null)
                    {
                        await DropSessionAsync(subscription, cancellationToken);
                        valid = false;
                        break;
                    }

                    deadlines[subscription.Id] = Earlier(
                        deadlines[subscription.Id],
                        after.Properties.ExpiresUtc!.Value);
                    if (!SameAuthority(before.Principal, after.Principal) ||
                        (authority is not null && !SameAuthority(authority, after.Principal)))
                    {
                        valid = false;
                        break;
                    } // A changed ticket needs a fresh authorization pass, never the old result.

                    if (!allowed)
                    {
                        if (topic.Scope != RealtimeScope.User)
                            await hub.Clients.Client(connectionId).AccessRevoked(subscription.Request.TenantId);
                        valid = false;
                        break;
                    }

                    authority = after.Principal;
                }

                cancellationToken.ThrowIfCancellationRequested();
                valid &= candidates.All(value => deadlines[value.Id] > clock.GetUtcNow());
                if (!valid)
                {
                    await subscriptions.RemoveAsync(
                        connectionId,
                        candidates.Select(value => value.Id).ToArray(),
                        cancellationToken);
                    continue;
                }

                // This fences completed slow work; it cannot make a network send atomic with a concurrent authority write.
                recipientCount++;
                await hub.Clients.Client(connectionId).Changed(change);
            }

            outcome = recipientCount > 0 ? RealtimeDeliveryOutcome.Sent : RealtimeDeliveryOutcome.NoRecipients;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = RealtimeDeliveryOutcome.Cancelled;
            throw;
        }
        finally
        {
            metrics?.RecordDelivery(topic, outcome, recipientCount, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<AuthenticationTicket?> LiveTicketAsync(
        RealtimeSubscription subscription,
        DateTimeOffset deadline,
        ClaimsPrincipal identity,
        CancellationToken ct)
    {
        var ticket = await tickets.RetrieveAsync(subscription.SessionId);
        var revoked = await subscriptions.IsSessionRevokedAsync(subscription.SessionId, ct);
        ct.ThrowIfCancellationRequested();
        return !revoked && deadline > clock.GetUtcNow() && ticket?.Properties.ExpiresUtc > clock.GetUtcNow() &&
            ticket.Principal.Identity?.IsAuthenticated == true &&
            ticket.Principal.FindFirst("sub")?.Value == identity.FindFirst("sub")?.Value &&
            ticket.Principal.FindFirst("iss")?.Value == identity.FindFirst("iss")?.Value &&
            ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)?.Value == subscription.SessionId
                ? ticket
                : null;
    }

    private async Task DropSessionAsync(RealtimeSubscription subscription, CancellationToken ct)
    {
        await subscriptions.RemoveAsync(subscription.ConnectionId, [subscription.Id], ct);
        if (await subscriptions.IsSessionRevokedAsync(subscription.SessionId, ct))
            await hub.Clients.Client(subscription.ConnectionId).SessionRevoked();
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static bool SameAuthority(ClaimsPrincipal left, ClaimsPrincipal right) => left.Claims
        .Select(value => (value.Type, value.Value))
        .Order()
        .SequenceEqual(right.Claims.Select(value => (value.Type, value.Value)).Order());
}
