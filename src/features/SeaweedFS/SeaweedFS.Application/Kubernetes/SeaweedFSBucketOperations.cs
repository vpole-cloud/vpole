using Hangfire;
using Hangfire.Storage;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

// A deployment claim does not cover the independent retention job. Serialize every bucket
// native writer with cleanup, and reload durable intent only after owning the shared lock.
internal sealed class SeaweedFSBucketOperations(
    JobStorage storage,
    LoadSeaweedFSBucket loadBucket
)
{
    public Task<bool> RunAsync(
        SeaweedFSBucket bucket,
        Func<SeaweedFSBucket?, Task<bool>> operation,
        CancellationToken ct) => Task.Run(() => Run(bucket, operation, ct), ct);

    private bool Run(
        SeaweedFSBucket bucket,
        Func<SeaweedFSBucket?, Task<bool>> operation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = storage.GetConnection();
        IDisposable bucketLock;
        try
        {
            bucketLock = connection.AcquireDistributedLock(
                $"pole:seaweedfs:bucket:{bucket.TenantId}:{bucket.SeaweedFSId}:{bucket.Id}",
                TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            return false;
        }

        using (bucketLock)
        {
            // Hangfire.Mongo tracks nested locks per thread. Keep acquisition and release
            // on this worker while the Mongo read and native operations continue asynchronously.
            ct.ThrowIfCancellationRequested();
            var current = loadBucket(bucket.TenantId, bucket.SeaweedFSId, bucket.Id, ct).GetAwaiter().GetResult();
            return operation(current).GetAwaiter().GetResult();
        }
    }
}
