using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public interface IRealtimeClient
{
    Task Changed(RealtimeChange change);
    Task SessionRevoked();
    Task AccessRevoked(string? tenantId);
}

[Authorize(AuthenticationSchemes = RealtimeFeature.CookieScheme)]
public sealed class RealtimeHub(
    IRealtimeSubscriptions subscriptions,
    IRealtimeTicketStore tickets,
    RealtimeAccess access,
    IEnumerable<IRealtimeTopicLifecycle> lifecycles,
    TimeProvider clock) : Hub<IRealtimeClient>
{
    private const string OwnedSubscriptions = "pole.realtime.subscriptions";

    public async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> Subscribe(RealtimeSubscriptionRequest[] requests)
    {
        if (requests is null || requests.Length > 64 || requests.Length == 0 || requests.Any(request => request is null || string.IsNullOrWhiteSpace(request.Topic)))
            throw new HubException("Provide between one and 64 distinct subscriptions.");
        requests = requests.Select(request => ObjectId.TryParse(request.TenantId, out var tenantId)
            ? request with { TenantId = tenantId.ToString() } : request).ToArray();
        if (requests.Distinct().Count() != requests.Length)
            throw new HubException("Provide between one and 64 distinct subscriptions.");
        var sessionId = Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value;
        var ticket = sessionId is null ? null : await tickets.RetrieveAsync(sessionId);
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow())
            throw new HubException("The realtime session is unavailable.");
        foreach (var request in requests)
            if (!await access.AllowsAsync(ticket.Principal, request, Context.ConnectionAborted))
                throw new HubException("This realtime subscription is unavailable.");

        // Replacement fails closed: an unsuccessful owner/storage call cannot leave
        // acknowledged old coverage in the distributed registry.
        await subscriptions.RemoveAsync(Context.ConnectionId, null, Context.ConnectionAborted);
        await ReleaseInterestsAsync();
        var owned = requests.Select(request => new RealtimeSubscription(Guid.NewGuid().ToString("N"),
            Context.ConnectionId, sessionId!, request, expiry)).ToArray();
        var acknowledgements = new List<RealtimeSubscriptionAcknowledgement>();
        try
        {
            foreach (var subscription in owned)
            {
                var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
                var covered = lifecycle is null || await lifecycle.SubscribeAsync(subscription, ticket.Principal, Context.ConnectionAborted);
                acknowledgements.Add(new(subscription.Id, subscription.Request.Topic, subscription.Request.TenantId,
                    subscription.Request.ResourceId, covered));
            }
            await subscriptions.ReplaceAsync(Context.ConnectionId, owned, Context.ConnectionAborted);
            Context.Items[OwnedSubscriptions] = owned;
            return acknowledgements;
        }
        catch
        {
            try { await subscriptions.RemoveAsync(Context.ConnectionId, null, CancellationToken.None); }
            finally { await Task.WhenAll(owned.Select(ReleaseInterestAsync)); }
            throw;
        }
    }

    public async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> Heartbeat()
    {
        try
        {
            var owned = Context.Items.TryGetValue(OwnedSubscriptions, out var value) && value is RealtimeSubscription[] current
                ? current : [];
            var sessionId = Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value;
            var ticket = sessionId is null ? null : await tickets.RetrieveAsync(sessionId);
            if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow())
                return await RejectUnavailableSessionAsync(sessionId);

            owned = owned.Select(subscription => subscription with
            {
                ExpiresAtUtc = subscription.ExpiresAtUtc < expiry ? subscription.ExpiresAtUtc : expiry
            }).ToArray();
            owned = await RetainAuthorizedInterestsAsync(owned, ticket.Principal);
            var coverage = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var subscription in owned)
            {
                var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
                coverage[subscription.Id] = lifecycle is null ||
                    await lifecycle.RenewAsync(subscription, ticket.Principal, Context.ConnectionAborted);
            }

            // Renewal may take time. Re-read the live session before acknowledging distributed coverage.
            ticket = await tickets.RetrieveAsync(sessionId!);
            if (ticket?.Properties.ExpiresUtc is not { } currentExpiry || currentExpiry <= clock.GetUtcNow())
                return await RejectUnavailableSessionAsync(sessionId);
            owned = await RetainAuthorizedInterestsAsync(owned, ticket.Principal);
            owned = owned.Select(subscription => subscription with
            {
                ExpiresAtUtc = subscription.ExpiresAtUtc < currentExpiry ? subscription.ExpiresAtUtc : currentExpiry
            }).ToArray();
            await subscriptions.ReplaceAsync(Context.ConnectionId, owned, Context.ConnectionAborted);
            Context.Items[OwnedSubscriptions] = owned;
            return owned.Select(subscription => new RealtimeSubscriptionAcknowledgement(subscription.Id,
                subscription.Request.Topic, subscription.Request.TenantId, subscription.Request.ResourceId, coverage[subscription.Id])).ToArray();
        }
        catch (RealtimeSessionUnavailableException)
        {
            return await RevokeSessionInterestsAsync();
        }
    }

    private async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> RejectUnavailableSessionAsync(string? sessionId)
    {
        if (sessionId is not null && await subscriptions.IsSessionRevokedAsync(sessionId, Context.ConnectionAborted))
            return await RevokeSessionInterestsAsync();
        try { await subscriptions.RemoveAsync(Context.ConnectionId, null, Context.ConnectionAborted); }
        finally { await ReleaseInterestsAsync(); }
        // Ticket expiry, corruption or storage loss can be recovered through a fresh cookie bootstrap.
        throw new HubException("The realtime session is unavailable.");
    }

    private async Task<RealtimeSubscription[]> RetainAuthorizedInterestsAsync(
        RealtimeSubscription[] owned,
        ClaimsPrincipal principal)
    {
        var deniedTenants = new HashSet<string?>(StringComparer.Ordinal);
        foreach (var subscription in owned)
            if (!await access.AllowsAsync(principal, subscription.Request, Context.ConnectionAborted))
                deniedTenants.Add(subscription.Request.TenantId);

        // A denial on one request fences every owned interest in that tenant, including earlier checks.
        var removed = owned.Where(subscription => deniedTenants.Contains(subscription.Request.TenantId) ||
            subscription.ExpiresAtUtc <= clock.GetUtcNow()).ToArray();
        if (removed.Length == 0) return owned;
        var removedIds = removed.Select(subscription => subscription.Id).ToHashSet(StringComparer.Ordinal);
        var retained = owned.Where(subscription => !removedIds.Contains(subscription.Id)).ToArray();
        Context.Items[OwnedSubscriptions] = retained;
        try
        {
            await subscriptions.RemoveAsync(Context.ConnectionId, removedIds, Context.ConnectionAborted);
        }
        finally
        {
            try { await Task.WhenAll(removed.Select(ReleaseInterestAsync)); }
            finally
            {
                foreach (var tenantId in deniedTenants) await Clients.Caller.AccessRevoked(tenantId);
            }
        }
        return retained;
    }

    private async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> RevokeSessionInterestsAsync()
    {
        try { await subscriptions.RemoveAsync(Context.ConnectionId, null, Context.ConnectionAborted); }
        finally
        {
            try { await ReleaseInterestsAsync(); }
            finally { await Clients.Caller.SessionRevoked(); }
        }
        return [];
    }

    public async Task Unsubscribe()
    {
        try { await subscriptions.RemoveAsync(Context.ConnectionId, null, Context.ConnectionAborted); }
        finally { await ReleaseInterestsAsync(); }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try { await subscriptions.RemoveAsync(Context.ConnectionId, null, CancellationToken.None); }
        finally { await ReleaseInterestsAsync(); }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task ReleaseInterestsAsync()
    {
        if (Context.Items.Remove(OwnedSubscriptions, out var value) && value is RealtimeSubscription[] owned)
            await Task.WhenAll(owned.Select(ReleaseInterestAsync));
    }

    private async Task ReleaseInterestAsync(RealtimeSubscription subscription)
    {
        var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
        if (lifecycle is not null) await lifecycle.UnsubscribeAsync(subscription, CancellationToken.None);
    }
}
