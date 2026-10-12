using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
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
        if (requests is null || requests.Length > 64 || requests.Length == 0 || requests.Any(request => request is null || string.IsNullOrWhiteSpace(request.Topic)) ||
            requests.Distinct().Count() != requests.Length)
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
