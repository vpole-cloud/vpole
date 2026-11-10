using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Tests;

internal sealed class MemoryTickets : IRealtimeTicketStore
{
    public ConcurrentDictionary<string, AuthenticationTicket> Tickets { get; } = new();

    public Exception? RetrieveFailure { get; set; }

    public bool ReadAsUnavailable { get; set; }

    public int RenewCalls { get; private set; }

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = ticket.Principal.FindFirst(RealtimeFeature.SessionClaim)!.Value;
        Tickets[key] = ticket;
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        RenewCalls++;
        Tickets[key] = ticket;
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (RetrieveFailure is { } failure)
            throw failure;
        if (ReadAsUnavailable)
            return Task.FromResult<AuthenticationTicket?>(null);
        return Task.FromResult(Tickets.GetValueOrDefault(key));
    }

    public Task RemoveAsync(string key)
    {
        Tickets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class MemorySubscriptions : IRealtimeSubscriptions
{
    public Dictionary<string, RealtimeSubscription[]> Connections { get; } = new();

    public Exception? ReplaceFailure { get; set; }

    public Exception? RevocationCheckFailure { get; set; }

    public Action? OnRevocationCheck { get; set; }

    public HashSet<string> Revoked { get; } = new(StringComparer.Ordinal);

    public List<string> RevocationChecks { get; } = [];

    public int ReplaceCalls { get; private set; }

    public int RemoveCalls { get; private set; }

    public Task ReplaceAsync(
        string connectionId,
        IReadOnlyList<RealtimeSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        ReplaceCalls++;
        if (ReplaceFailure is { } failure)
            throw failure;
        if (subscriptions.Any(subscription => Revoked.Contains(subscription.SessionId)))
            throw new RealtimeSessionUnavailableException();
        Connections[connectionId] = subscriptions.ToArray();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(
        string connectionId,
        IReadOnlyCollection<string>? subscriptionIds,
        CancellationToken cancellationToken)
    {
        RemoveCalls++;
        if (subscriptionIds is null)
            Connections.Remove(connectionId);
        else if (Connections.TryGetValue(connectionId, out var current))
            Connections[connectionId]
                = current.Where(subscription => !subscriptionIds.Contains(subscription.Id)).ToArray();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RealtimeSubscription>> FindAsync(
        string topic,
        string? tenantId,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RealtimeSubscription>>(
        Connections.Values
            .SelectMany(value => value)
            .Where(subscription => subscription.Request.Topic == topic && subscription.Request.TenantId == tenantId)
            .ToArray());

    public Task<IReadOnlyList<RealtimeSubscription>> FindAllAsync(string topic, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RealtimeSubscription>>(
            Connections.Values
                .SelectMany(value => value)
                .Where(subscription => subscription.Request.Topic == topic)
                .ToArray());

    public Task<IReadOnlyList<string>> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        Revoked.Add(sessionId);
        var ids = Connections.Where(pair => pair.Value.Any(subscription => subscription.SessionId == sessionId))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in ids)
            Connections.Remove(id);
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<bool> IsSessionRevokedAsync(string sessionId, CancellationToken cancellationToken)
    {
        RevocationChecks.Add(sessionId);
        OnRevocationCheck?.Invoke();
        if (RevocationCheckFailure is { } failure)
            throw failure;
        return Task.FromResult(Revoked.Contains(sessionId));
    }
}

internal static class IdentityFixture
{
    public static ClaimsPrincipal User(string id = "viewer", bool admin = false) => new(
        new ClaimsIdentity(
            new[]
            {
                new Claim("sub", id), new Claim("iss", "https://issuer.test"),
                new Claim(RealtimeFeature.SessionClaim, id),
            }.Concat(admin ? [new Claim("roles", "pole-platform-admin")] : []),
            RealtimeFeature.CookieScheme));
}
