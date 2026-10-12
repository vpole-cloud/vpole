using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Tests;

internal sealed class MemoryTickets : IRealtimeTicketStore
{
    public ConcurrentDictionary<string, AuthenticationTicket> Tickets { get; } = new();
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)!.Value;
        Tickets[key] = ticket;
        return Task.FromResult(key);
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) { Tickets[key] = ticket; return Task.CompletedTask; }
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => Task.FromResult(Tickets.GetValueOrDefault(key));
    public Task RemoveAsync(string key) { Tickets.TryRemove(key, out _); return Task.CompletedTask; }
}

internal sealed class MemorySubscriptions : IRealtimeSubscriptions
{
    public Dictionary<string, RealtimeSubscription[]> Connections { get; } = new();
    public Task ReplaceAsync(string connectionId, IReadOnlyList<RealtimeSubscription> subscriptions, CancellationToken cancellationToken)
    { Connections[connectionId] = subscriptions.ToArray(); return Task.CompletedTask; }
    public Task RemoveAsync(string connectionId, IReadOnlyCollection<string>? subscriptionIds, CancellationToken cancellationToken)
    {
        if (subscriptionIds is null) Connections.Remove(connectionId);
        else if (Connections.TryGetValue(connectionId, out var current))
            Connections[connectionId] = current.Where(subscription => !subscriptionIds.Contains(subscription.Id)).ToArray();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RealtimeSubscription>> FindAsync(string topic, string? tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RealtimeSubscription>>(Connections.Values.SelectMany(value => value)
            .Where(subscription => subscription.Request.Topic == topic && subscription.Request.TenantId == tenantId).ToArray());
    public Task<IReadOnlyList<string>> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var ids = Connections.Where(pair => pair.Value.Any(subscription => subscription.SessionId == sessionId)).Select(pair => pair.Key).ToArray();
        foreach (var id in ids) Connections.Remove(id);
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }
}

internal static class IdentityFixture
{
    public static ClaimsPrincipal User(string id = "viewer", bool admin = false) => new(new ClaimsIdentity(
        new[] { new Claim("sub", id), new Claim("iss", "https://issuer.test"), new Claim(RealtimeFeature.SessionClaim, id) }
            .Concat(admin ? [new Claim("roles", "pole-platform-admin")] : []), RealtimeFeature.CookieScheme));
}
