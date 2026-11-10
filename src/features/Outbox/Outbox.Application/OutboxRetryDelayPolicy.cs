namespace Pole.Features.Outbox.Application;

public sealed class OutboxRetryDelayPolicy(
    OutboxSettings settings
)
{
    public TimeSpan Calculate(string messageId, int nextAttemptCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(nextAttemptCount, 1);

        var exponent = Math.Min(nextAttemptCount - 1, 62);
        var exponentialTicks = settings.InitialRetryDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = Math.Min(exponentialTicks, settings.MaximumRetryDelay.Ticks);
        var jitteredTicks = cappedTicks * JitterFactor(messageId, nextAttemptCount);

        return TimeSpan.FromTicks((long)Math.Min(jitteredTicks, settings.MaximumRetryDelay.Ticks));
    }

    private static double JitterFactor(string messageId, int attemptCount)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        foreach (var character in messageId)
        {
            hash ^= character;
            hash *= prime;
        }

        hash ^= (uint)attemptCount;
        hash *= prime;

        return 0.9 + hash % 201 / 1000d;
    }
}
