using System.Diagnostics;
using System.Threading;

namespace Pole.Observability;

public sealed record TraceContextSnapshot(string TraceId, string CorrelationId);

public interface ITraceContextCarrier
{
    string TraceId { get; }
    string CorrelationId { get; }
}

public static class TraceHeaders
{
    public const string CorrelationId = "X-Correlation-ID";
    public const string TraceId = "X-Trace-ID";
}

public static class TraceContext
{
    private const int MaxCorrelationIdLength = 128;
    private static readonly AsyncLocal<TraceContextSnapshot?> CurrentContext = new();

    public static TraceContextSnapshot? Current => CurrentContext.Value;

    public static string CurrentTraceId => Current?.TraceId ?? NormalizeTraceId(Activity.Current?.TraceId.ToString());

    public static string CurrentCorrelationId => Current?.CorrelationId ?? CreateCorrelationId();

    public static TraceContextSnapshot Create(string? traceId = null, string? correlationId = null)
        => new(
            NormalizeTraceId(traceId),
            NormalizeCorrelationId(correlationId));

    public static TraceContextSnapshot FromCarrierOrCurrent(ITraceContextCarrier? carrier)
        => Create(
            carrier?.TraceId ?? Current?.TraceId,
            carrier?.CorrelationId ?? Current?.CorrelationId);

    /// <summary>
    /// Stores the trace context for the current async operation and restores the previous value when disposed.
    /// </summary>
    public static IDisposable Push(TraceContextSnapshot context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new RestoreScope(previous);
    }

    public static string NormalizeCorrelationId(string? correlationId)
    {
        var trimmed = correlationId?.Trim();
        return IsSafeCorrelationId(trimmed) ? trimmed! : CreateCorrelationId();
    }

    public static bool IsSafeCorrelationId(string? correlationId)
        => !string.IsNullOrWhiteSpace(correlationId)
           && correlationId.Length <= MaxCorrelationIdLength
           && !correlationId.Contains('\r')
           && !correlationId.Contains('\n');

    private static string NormalizeTraceId(string? traceId)
    {
        var trimmed = traceId?.Trim();
        return IsW3CTraceId(trimmed) ? trimmed!.ToLowerInvariant() : ActivityTraceId.CreateRandom().ToString();
    }

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("N");

    private static bool IsW3CTraceId(string? traceId)
    {
        if (traceId is not { Length: 32 })
        {
            return false;
        }

        var hasNonZero = false;
        foreach (var character in traceId)
        {
            var isHex =
                character is >= '0' and <= '9'
                || character is >= 'a' and <= 'f'
                || character is >= 'A' and <= 'F';

            if (!isHex)
            {
                return false;
            }

            hasNonZero |= character != '0';
        }

        return hasNonZero;
    }

    private sealed class RestoreScope(TraceContextSnapshot? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            CurrentContext.Value = previous;
        }
    }
}
