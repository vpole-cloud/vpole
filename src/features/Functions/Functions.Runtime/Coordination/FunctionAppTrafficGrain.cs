using Pole.Features.Functions.Runtime.Gateway;

namespace Pole.Features.Functions.Runtime.Coordination;

public sealed class FunctionAppTrafficGrain(
    FunctionGatewayOptions gatewayOptions,
    TimeProvider timeProvider
) : Grain, IFunctionAppTrafficGrain
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(1);
    private readonly Queue<(string RequestId, DateTimeOffset ExpiresAtUtc)> decisionExpirations = new();

    private readonly Dictionary<string, CachedTrafficDecision> decisions = new(StringComparer.Ordinal);
    private double availableTokens;
    private FunctionAppCoordinationKey key = null!;
    private long lastRefillTimestamp;

    public Task<FunctionTrafficDecision> TryAcquireAsync(FunctionTrafficRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredDecisions(now);
        RefillTokens();
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.HandlerName) ||
            request.Cost <= 0 || request.Cost > gatewayOptions.PublicBurst)
            return Task.FromResult(
                new FunctionTrafficDecision(
                    FunctionTrafficDecisionKind.Rejected,
                    FunctionTrafficRejection.InvalidRequest,
                    0));

        var fingerprint = new TrafficRequestFingerprint(request.HandlerName, request.Cost);
        if (decisions.TryGetValue(request.RequestId, out var cached))
            return Task.FromResult(
                cached.Fingerprint == fingerprint
                    ? cached.Decision
                    : new FunctionTrafficDecision(
                        FunctionTrafficDecisionKind.Rejected,
                        FunctionTrafficRejection.IdempotencyConflict,
                        0));

        FunctionTrafficDecision decision;
        if (availableTokens >= request.Cost)
        {
            availableTokens -= request.Cost;
            decision = new FunctionTrafficDecision(
                FunctionTrafficDecisionKind.Granted,
                FunctionTrafficRejection.None,
                0);
        }
        else
        {
            var missingTokens = request.Cost - availableTokens;
            var retryAfterMilliseconds = (int)Math.Clamp(
                Math.Ceiling(missingTokens / gatewayOptions.PublicRequestsPerSecond * 1000),
                1,
                int.MaxValue);
            decision = new FunctionTrafficDecision(
                FunctionTrafficDecisionKind.Rejected,
                FunctionTrafficRejection.RateLimitExceeded,
                retryAfterMilliseconds);
        }

        if (decision.Kind == FunctionTrafficDecisionKind.Granted)
        {
            var expiresAtUtc = now.Add(IdempotencyRetention);
            decisions.Add(request.RequestId, new CachedTrafficDecision(fingerprint, decision, expiresAtUtc));
            decisionExpirations.Enqueue((request.RequestId, expiresAtUtc));
            DelayDeactivation(IdempotencyRetention);
        }

        return Task.FromResult(decision);
    }

    public Task<FunctionTrafficSnapshot> GetSnapshotAsync()
    {
        RefillTokens();
        return Task.FromResult(
            new FunctionTrafficSnapshot(
                key,
                availableTokens,
                gatewayOptions.PublicRequestsPerSecond,
                gatewayOptions.PublicBurst));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!FunctionAppCoordinationKey.TryParse(this.GetPrimaryKeyString(), out var parsedKey))
            throw new InvalidOperationException(
                $"Invalid Function app traffic grain key '{this.GetPrimaryKeyString()}'.");

        key = parsedKey!;
        availableTokens = gatewayOptions.PublicBurst;
        lastRefillTimestamp = timeProvider.GetTimestamp();
        return base.OnActivateAsync(cancellationToken);
    }

    private void RefillTokens()
    {
        var timestamp = timeProvider.GetTimestamp();
        var elapsed = timeProvider.GetElapsedTime(lastRefillTimestamp, timestamp);
        lastRefillTimestamp = timestamp;
        availableTokens = Math.Min(
            gatewayOptions.PublicBurst,
            availableTokens + elapsed.TotalSeconds * gatewayOptions.PublicRequestsPerSecond);
    }

    private void PurgeExpiredDecisions(DateTimeOffset now)
    {
        while (decisionExpirations.TryPeek(out var expiration) && expiration.ExpiresAtUtc <= now)
        {
            decisionExpirations.Dequeue();
            if (decisions.TryGetValue(expiration.RequestId, out var cached) &&
                cached.ExpiresAtUtc == expiration.ExpiresAtUtc)
                decisions.Remove(expiration.RequestId);
        }
    }

    private sealed record TrafficRequestFingerprint(
        string HandlerName,
        int Cost
    );

    private sealed record CachedTrafficDecision(
        TrafficRequestFingerprint Fingerprint,
        FunctionTrafficDecision Decision,
        DateTimeOffset ExpiresAtUtc
    );
}
