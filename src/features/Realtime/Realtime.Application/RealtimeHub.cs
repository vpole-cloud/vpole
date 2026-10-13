using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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
    TimeProvider clock,
    RealtimeMetrics? metrics = null) : Hub<IRealtimeClient>
{
    private const string ClientLifecycle = "pole.realtime.client-lifecycle";
    private const string AcknowledgedCoverage = "pole.realtime.coverage";
    private sealed class ClientReportState
    {
        public SemaphoreSlim Gate { get; } = new(1);
        public DateTimeOffset? LastAcceptedAtUtc { get; set; }
    }

    /// <summary>Optional aggregate diagnostics. False means coalesced by the ten-second limit.</summary>
    public async Task<bool> ReportClientLifecycle(RealtimeClientLifecycleReport report)
    {
        if (report is null || report.Reconnects is < 0 or > 100 || report.Fallbacks is < 0 or > 100 || report is { Reconnects: 0, Fallbacks: 0 })
            throw new HubException("Provide bounded client lifecycle counts.");
        ClientReportState state;
        lock (Context.Items)
        {
            if (Context.Items.TryGetValue(ClientLifecycle, out var value) && value is ClientReportState existing) state = existing;
            else { state = new(); Context.Items[ClientLifecycle] = state; }
        }
        await state.Gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var sessionId = Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value;
            var ticket = sessionId is null ? null : await tickets.RetrieveAsync(sessionId);
            if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow() ||
                ticket.Principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(Context.User?.FindFirst("sub")?.Value) ||
                ticket.Principal.FindFirst("sub")?.Value != Context.User?.FindFirst("sub")?.Value ||
                ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)?.Value != sessionId ||
                await subscriptions.IsSessionRevokedAsync(sessionId!, Context.ConnectionAborted))
                throw new HubException("The realtime session is unavailable.");
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            if (expiry <= now) throw new HubException("The realtime session is unavailable.");
            if (state.LastAcceptedAtUtc is { } last && now - last < TimeSpan.FromSeconds(10)) return false;
            state.LastAcceptedAtUtc = now;
            metrics?.RecordClientLifecycle(report);
            return true;
        }
        finally { state.Gate.Release(); }
    }

    public override Task OnConnectedAsync()
    {
        metrics?.RecordConnection(true);
        return base.OnConnectedAsync();
    }

    private const string OwnedSubscriptions = "pole.realtime.subscriptions";

    public async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> Subscribe(RealtimeSubscriptionRequest[] requests)
    {
        if (requests is null || requests.Length > 64 || requests.Length == 0 || requests.Any(request =>
                request is null || string.IsNullOrWhiteSpace(request.Topic) || request.Topic.Length > 100 || request.ResourceId is { Length: > 200 }))
            throw new HubException("Provide between one and 64 distinct subscriptions.");
        requests = requests.Select(request => ObjectId.TryParse(request.TenantId, out var tenantId)
            ? request with { TenantId = tenantId.ToString() } : request).ToArray();
        if (requests.Distinct().Count() != requests.Length)
            throw new HubException("Provide between one and 64 distinct subscriptions.");
        // Invalid scope shapes are request errors, not revocation of an unrelated workspace.
        foreach (var request in requests)
            if (access.FindTopic(request.Topic) is { } topic && (topic.Scope switch
            {
                RealtimeScope.Tenant => !ObjectId.TryParse(request.TenantId, out _),
                RealtimeScope.Platform => request.TenantId is not null,
                RealtimeScope.User => request.TenantId is not null || string.IsNullOrWhiteSpace(request.ResourceId),
                _ => true
            }))
                throw new HubException("This realtime subscription has an invalid scope.");
        try
        {
            var sessionId = Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value;
            var ticket = await ReadLiveSessionAsync();
            var expiry = ticket.Properties.ExpiresUtc!.Value;
            var deniedScopes = await FindDeniedScopesAsync(requests, ticket.Principal);
            await RequireUnchangedAuthorityAsync(ticket.Principal);
            if (deniedScopes.Count > 0)
            {
                await RetainInterestsAsync(GetOwnedSubscriptions(), deniedScopes);
                throw new HubException("This realtime subscription is unavailable.");
            }

            // Replacement fails closed: an unsuccessful owner/storage call cannot leave
            // acknowledged old coverage in the distributed registry.
            await subscriptions.RemoveAsync(Context.ConnectionId, null, Context.ConnectionAborted);
            await ReleaseInterestsAsync();
            var owned = requests.Select(request => new RealtimeSubscription(Guid.NewGuid().ToString("N"),
                Context.ConnectionId, sessionId!, request, expiry)).ToArray();
            var acknowledgements = new List<RealtimeSubscriptionAcknowledgement>();
            Context.Items[OwnedSubscriptions] = owned;
            try
            {
                foreach (var subscription in owned)
                {
                    var covered = false;
                    if (access.IsAvailable(subscription.Request.Topic))
                    {
                        var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
                        covered = lifecycle is null || await lifecycle.SubscribeAsync(subscription, ticket.Principal, Context.ConnectionAborted);
                    }
                    acknowledgements.Add(new(subscription.Id, subscription.Request.Topic, subscription.Request.TenantId,
                        subscription.Request.ResourceId, covered));
                }
                await ReplaceAvailableInterestsAsync(owned);
                owned = await FenceAcknowledgementsAsync(owned);
                var retainedIds = owned.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
                acknowledgements.RemoveAll(value => !retainedIds.Contains(value.SubscriptionId));
                Context.Items[OwnedSubscriptions] = owned;
                foreach (var subscription in owned) metrics?.RecordSubscription(access.FindTopic(subscription.Request.Topic), true);
                RecordAcknowledgements(acknowledgements);
                return acknowledgements;
            }
            catch
            {
                await ClearInterestsAsync(Context.ConnectionAborted.IsCancellationRequested ? owned : null);
                throw;
            }
        }
        catch (RealtimeSessionUnavailableException) { return await RevokeSessionInterestsAsync(); }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        { await ClearInterestsAsync(); throw; }
    }

    public async Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> Heartbeat()
    {
        var owned = GetOwnedSubscriptions();
        try
        {
            var ticket = await ReadLiveSessionAsync();
            var expiry = ticket.Properties.ExpiresUtc!.Value;

            owned = owned.Select(subscription => subscription with
            {
                ExpiresAtUtc = subscription.ExpiresAtUtc < expiry ? subscription.ExpiresAtUtc : expiry
            }).ToArray();
            owned = await RetainAuthorizedInterestsAsync(owned, ticket.Principal);
            var coverage = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var subscription in owned)
            {
                if (!access.IsAvailable(subscription.Request.Topic))
                {
                    coverage[subscription.Id] = false;
                    continue;
                }
                var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
                coverage[subscription.Id] = lifecycle is null ||
                    await lifecycle.RenewAsync(subscription, ticket.Principal, Context.ConnectionAborted);
            }

            // Renewal may take time. Re-read the live session before acknowledging distributed coverage.
            ticket = await ReadLiveSessionAsync();
            var currentExpiry = ticket.Properties.ExpiresUtc!.Value;
            owned = await RetainAuthorizedInterestsAsync(owned, ticket.Principal);
            owned = owned.Select(subscription => subscription with
            {
                ExpiresAtUtc = subscription.ExpiresAtUtc < currentExpiry ? subscription.ExpiresAtUtc : currentExpiry
            }).ToArray();
            await ReplaceAvailableInterestsAsync(owned);
            owned = await FenceAcknowledgementsAsync(owned);
            Context.Items[OwnedSubscriptions] = owned;
            var acknowledgements = owned.Select(subscription => new RealtimeSubscriptionAcknowledgement(subscription.Id,
                subscription.Request.Topic, subscription.Request.TenantId, subscription.Request.ResourceId, coverage[subscription.Id])).ToArray();
            RecordAcknowledgements(acknowledgements);
            return acknowledgements;
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        { await ClearInterestsAsync(owned); throw; }
        catch (RealtimeSessionUnavailableException)
        {
            return await RevokeSessionInterestsAsync();
        }
    }

    private async Task<AuthenticationTicket> ReadLiveSessionAsync()
    {
        var sessionId = Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value;
        var ticket = sessionId is null ? null : await tickets.RetrieveAsync(sessionId);
        var revoked = sessionId is not null && await subscriptions.IsSessionRevokedAsync(sessionId, Context.ConnectionAborted);
        Context.ConnectionAborted.ThrowIfCancellationRequested();
        if (revoked) throw new RealtimeSessionUnavailableException();
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow() ||
            ticket.Principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(Context.User?.FindFirst("sub")?.Value) ||
            ticket.Principal.FindFirst("sub")?.Value != Context.User?.FindFirst("sub")?.Value ||
            ticket.Principal.FindFirst("iss")?.Value != Context.User?.FindFirst("iss")?.Value ||
            ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)?.Value != sessionId)
        {
            await ClearInterestsAsync();
            // Transport expiry/loss requests cookie bootstrap, never revocation of the ordinary workspace.
            throw new HubException("The realtime session is unavailable.");
        }
        return ticket;
    }

    private async Task RequireUnchangedAuthorityAsync(ClaimsPrincipal principal)
    {
        var current = await ReadLiveSessionAsync();
        if (SameAuthority(principal, current.Principal)) return;
        await ClearInterestsAsync();
        throw new HubException("The realtime session is unavailable.");
    }

    private async Task<RealtimeSubscription[]> FenceAcknowledgementsAsync(RealtimeSubscription[] owned)
    {
        var ticket = await ReadLiveSessionAsync();
        var denied = await FindDeniedScopesAsync(owned.Select(value => value.Request), ticket.Principal);
        await RequireUnchangedAuthorityAsync(ticket.Principal);
        owned = await RetainInterestsAsync(owned, denied);
        // Removing denied/expired interests may itself await provider or registry work.
        var current = await ReadLiveSessionAsync();
        if (!SameAuthority(ticket.Principal, current.Principal) ||
            owned.Any(value => value.ExpiresAtUtc > current.Properties.ExpiresUtc))
        {
            await ClearInterestsAsync();
            throw new HubException("The realtime session is unavailable.");
        }
        if (owned.Any(value => value.ExpiresAtUtc <= clock.GetUtcNow()))
        {
            await ClearInterestsAsync();
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return [];
        }
        Context.ConnectionAborted.ThrowIfCancellationRequested();
        // These final reads fence completed work; an authority write and network acknowledgement cannot be atomic.
        return owned;
    }

    private async Task ClearInterestsAsync(RealtimeSubscription[]? pending = null)
    {
        var alreadyReleased = GetOwnedSubscriptions().Length == 0;
        try { await subscriptions.RemoveAsync(Context.ConnectionId, null, CancellationToken.None); }
        finally
        {
            await ReleaseInterestsAsync();
            // A non-cooperative provider can finish its write after OnDisconnected removed local context.
            if (alreadyReleased && pending is not null) await Task.WhenAll(pending.Select(ReleaseInterestAsync));
        }
    }

    private static bool SameAuthority(ClaimsPrincipal left, ClaimsPrincipal right) =>
        left.Claims.Select(value => (value.Type, value.Value)).Order().SequenceEqual(right.Claims.Select(value => (value.Type, value.Value)).Order());

    private async Task<RealtimeSubscription[]> RetainAuthorizedInterestsAsync(
        RealtimeSubscription[] owned,
        ClaimsPrincipal principal)
    {
        var deniedScopes = await FindDeniedScopesAsync(owned.Select(subscription => subscription.Request), principal);
        await RequireUnchangedAuthorityAsync(principal);
        return await RetainInterestsAsync(owned, deniedScopes);
    }

    private sealed record DeniedScope(
        RealtimeScope Scope,
        string? TenantId);

    private async Task<HashSet<DeniedScope>> FindDeniedScopesAsync(
        IEnumerable<RealtimeSubscriptionRequest> requests, ClaimsPrincipal principal)
    {
        var deniedScopes = new HashSet<DeniedScope>();
        foreach (var request in requests)
            if (access.FindTopic(request.Topic) is { } topic &&
                !await access.AllowsAsync(principal, request, Context.ConnectionAborted))
                deniedScopes.Add(new(topic.Scope, request.TenantId));
        return deniedScopes;
    }

    private async Task<RealtimeSubscription[]> RetainInterestsAsync(
        RealtimeSubscription[] owned, HashSet<DeniedScope> deniedScopes)
    {

        // A denial on one request fences every owned interest in that tenant, including earlier checks.
        var removed = owned.Where(subscription => deniedScopes.Any(scope => scope.Scope == RealtimeScope.Tenant
                ? subscription.Request.TenantId == scope.TenantId
                : access.FindTopic(subscription.Request.Topic)?.Scope == scope.Scope) ||
            subscription.ExpiresAtUtc <= clock.GetUtcNow()).ToArray();
        var removedIds = removed.Select(subscription => subscription.Id).ToHashSet(StringComparer.Ordinal);
        var retained = owned.Where(subscription => !removedIds.Contains(subscription.Id)).ToArray();
        Context.Items[OwnedSubscriptions] = retained;
        try
        {
            if (removedIds.Count > 0)
                await subscriptions.RemoveAsync(Context.ConnectionId, removedIds, Context.ConnectionAborted);
        }
        finally
        {
            try { await Task.WhenAll(removed.Select(ReleaseInterestAsync)); }
            finally
            {
                foreach (var scope in deniedScopes.Where(scope => scope.Scope != RealtimeScope.User))
                    await Clients.Caller.AccessRevoked(scope.TenantId);
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
        metrics?.RecordConnection(false);
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
        metrics?.RecordSubscription(access.FindTopic(subscription.Request.Topic), false);
        if (Context.Items.TryGetValue(AcknowledgedCoverage, out var value) && value is Dictionary<string, bool> states) states.Remove(subscription.Id);
        if (!access.IsAvailable(subscription.Request.Topic)) return;
        var lifecycle = lifecycles.SingleOrDefault(candidate => candidate.Topic == subscription.Request.Topic);
        if (lifecycle is not null) await lifecycle.UnsubscribeAsync(subscription, CancellationToken.None);
    }

    private void RecordAcknowledgements(IEnumerable<RealtimeSubscriptionAcknowledgement> acknowledgements)
    {
        if (!Context.Items.TryGetValue(AcknowledgedCoverage, out var value) || value is not Dictionary<string, bool> states)
        { states = new(StringComparer.Ordinal); Context.Items[AcknowledgedCoverage] = states; }
        foreach (var acknowledgement in acknowledgements)
        {
            if (!states.TryGetValue(acknowledgement.SubscriptionId, out var previous) || previous != acknowledgement.Covered)
                metrics?.RecordCoverage(access.FindTopic(acknowledgement.Topic), acknowledgement.Covered);
            states[acknowledgement.SubscriptionId] = acknowledgement.Covered;
        }
    }

    private RealtimeSubscription[] GetOwnedSubscriptions() =>
        Context.Items.TryGetValue(OwnedSubscriptions, out var value) && value is RealtimeSubscription[] current ? current : [];

    private async Task ReplaceAvailableInterestsAsync(RealtimeSubscription[] owned)
    {
        // Local acknowledgement state includes unavailable topics. Those never own provider or
        // Redis interests; the immutable registry keeps this distinction stable for the connection.
        var available = owned.Where(subscription => access.IsAvailable(subscription.Request.Topic)).ToArray();
        if (available.Length == 0 && Context.User?.FindFirst(RealtimeFeature.SessionClaim)?.Value is { } sessionId &&
            await subscriptions.IsSessionRevokedAsync(sessionId, Context.ConnectionAborted))
            throw new RealtimeSessionUnavailableException();
        await subscriptions.ReplaceAsync(Context.ConnectionId, available, Context.ConnectionAborted);
    }
}
