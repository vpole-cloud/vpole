using System.Security.Claims;

namespace Pole.Features.Realtime.Contracts;

public sealed record RealtimeSubscriptionRequest(
    string Topic,
    string? TenantId = null,
    string? ResourceId = null);

public sealed record RealtimeSubscriptionAcknowledgement(
    string SubscriptionId,
    string Topic,
    string? TenantId,
    string? ResourceId,
    bool Covered);

public sealed record RealtimeSubscription(
    string Id,
    string ConnectionId,
    string SessionId,
    RealtimeSubscriptionRequest Request,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Provider-owned validation and interest lifecycle; generic transport knows no provider types.</summary>
public interface IRealtimeTopicLifecycle
{
    string Topic { get; }
    Task<bool> SubscribeAsync(RealtimeSubscription subscription, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task UnsubscribeAsync(RealtimeSubscription subscription, CancellationToken cancellationToken);
}
