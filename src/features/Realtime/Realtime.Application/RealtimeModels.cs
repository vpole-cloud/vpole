using Microsoft.AspNetCore.Authentication.Cookies;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

/// <summary>Backed by distributed storage in production, with expiring connection interests.</summary>
public interface IRealtimeSubscriptions
{
    Task ReplaceAsync(string connectionId, IReadOnlyList<RealtimeSubscription> subscriptions, CancellationToken cancellationToken);
    Task RemoveAsync(string connectionId, IReadOnlyCollection<string>? subscriptionIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<RealtimeSubscription>> FindAsync(string topic, string? tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRealtimeTicketStore : ITicketStore;
