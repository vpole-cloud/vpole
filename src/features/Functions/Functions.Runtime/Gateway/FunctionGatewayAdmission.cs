namespace Pole.Features.Functions.Runtime.Gateway;

public interface IFunctionPendingAdmissionLimiter
{
    FunctionPendingAdmissionLease? TryAcquire(string revisionId, string handlerName, long reservedBytes);
    FunctionPendingAdmissionSnapshot GetSnapshot(string revisionId);
    FunctionScaleDownLease? TryBeginScaleDown(string revisionId);
}

public sealed record FunctionPendingAdmissionSnapshot(
    int Requests,
    long Bytes,
    bool Quiescing,
    IReadOnlyDictionary<string, int> HandlerRequests)
{
    public static readonly FunctionPendingAdmissionSnapshot Empty = new(
        0,
        0,
        false,
        new Dictionary<string, int>(StringComparer.Ordinal));
}

public sealed class FunctionPendingAdmissionLimiter(FunctionGatewayOptions options)
    : IFunctionPendingAdmissionLimiter
{
    private sealed class RevisionCounters
    {
        public int Requests;
        public long Bytes;
        public bool Quiescing;
        public Dictionary<string, int> HandlerRequests { get; } = new(StringComparer.Ordinal);
    }

    private readonly object sync = new();
    private readonly Dictionary<string, RevisionCounters> revisions = new(StringComparer.Ordinal);
    private int requests;
    private long bytes;

    public FunctionPendingAdmissionLease? TryAcquire(
        string revisionId,
        string handlerName,
        long reservedBytes)
    {
        if (reservedBytes < 0 || reservedBytes > options.MaxRequestBodyBytes)
            return null;
        lock (sync)
        {
            if (!revisions.TryGetValue(revisionId, out var revision))
                revision = new RevisionCounters();
            if (revision.Quiescing || requests >= options.MaxPendingRequests ||
                revision.Requests >= options.MaxPendingRequestsPerRevision ||
                bytes + reservedBytes > options.MaxPendingBytes ||
                revision.Bytes + reservedBytes > options.MaxPendingBytesPerRevision)
                return null;

            requests++;
            bytes += reservedBytes;
            revision.Requests++;
            revision.Bytes += reservedBytes;
            revision.HandlerRequests[handlerName] = revision.HandlerRequests.GetValueOrDefault(handlerName) + 1;
            revisions[revisionId] = revision;
            return new FunctionPendingAdmissionLease(() => Release(revisionId, handlerName, reservedBytes));
        }
    }

    public FunctionPendingAdmissionSnapshot GetSnapshot(string revisionId)
    {
        lock (sync)
            return revisions.TryGetValue(revisionId, out var revision)
                ? new FunctionPendingAdmissionSnapshot(
                    revision.Requests,
                    revision.Bytes,
                    revision.Quiescing,
                    new Dictionary<string, int>(revision.HandlerRequests, StringComparer.Ordinal))
                : FunctionPendingAdmissionSnapshot.Empty;
    }

    public FunctionScaleDownLease? TryBeginScaleDown(string revisionId)
    {
        lock (sync)
        {
            if (!revisions.TryGetValue(revisionId, out var revision))
            {
                revision = new RevisionCounters();
                revisions[revisionId] = revision;
            }
            if (revision.Requests != 0 || revision.Quiescing)
                return null;
            revision.Quiescing = true;
            return new FunctionScaleDownLease(() => EndScaleDown(revisionId));
        }
    }

    private void Release(string revisionId, string handlerName, long reservedBytes)
    {
        lock (sync)
        {
            if (!revisions.TryGetValue(revisionId, out var revision))
                return;
            requests--;
            bytes -= reservedBytes;
            revision.Requests--;
            revision.Bytes -= reservedBytes;
            var handlerRequests = revision.HandlerRequests[handlerName] - 1;
            if (handlerRequests == 0)
                revision.HandlerRequests.Remove(handlerName);
            else
                revision.HandlerRequests[handlerName] = handlerRequests;
            if (revision.Requests == 0 && !revision.Quiescing)
                revisions.Remove(revisionId);
        }
    }

    private void EndScaleDown(string revisionId)
    {
        lock (sync)
        {
            if (!revisions.TryGetValue(revisionId, out var revision))
                return;
            revision.Quiescing = false;
            if (revision.Requests == 0)
                revisions.Remove(revisionId);
        }
    }
}

public sealed class FunctionPendingAdmissionLease(Action release) : IDisposable
{
    private Action? release = release;
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

public sealed class FunctionScaleDownLease(Action release) : IDisposable
{
    private Action? release = release;
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

public interface IFunctionPublicRateLimiter
{
    bool TryAcquire(string functionRoute);
}

public sealed class FunctionPublicRateLimiter(FunctionGatewayOptions options, TimeProvider timeProvider)
    : IFunctionPublicRateLimiter
{
    private static readonly TimeSpan BucketRetention = TimeSpan.FromMinutes(10);
    private sealed class Bucket(double tokens, long timestamp)
    {
        public double Tokens = tokens;
        public long Timestamp = timestamp;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private int acquisitions;

    public bool TryAcquire(string functionRoute)
    {
        lock (sync)
        {
            var now = timeProvider.GetTimestamp();
            acquisitions++;
            if ((acquisitions & 1023) == 0)
            {
                foreach (var stale in buckets
                             .Where(entry => timeProvider.GetElapsedTime(entry.Value.Timestamp, now) > BucketRetention)
                             .Select(entry => entry.Key)
                             .ToArray())
                    buckets.Remove(stale);
            }
            if (!buckets.TryGetValue(functionRoute, out var bucket))
                bucket = new Bucket(options.PublicBurst, now);
            var elapsed = timeProvider.GetElapsedTime(bucket.Timestamp, now).TotalSeconds;
            bucket.Tokens = Math.Min(options.PublicBurst, bucket.Tokens + elapsed * options.PublicRequestsPerSecond);
            bucket.Timestamp = now;
            if (bucket.Tokens < 1)
            {
                buckets[functionRoute] = bucket;
                return false;
            }

            bucket.Tokens--;
            buckets[functionRoute] = bucket;
            return true;
        }
    }
}
