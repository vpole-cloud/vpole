using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using k8s;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Kubernetes;
using Pole.Features.SeaweedFS.Application.Kubernetes;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Testing;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests.Integration;

[Collection("seaweedfs-mongo")]
public sealed class SeaweedFSBucketConcurrencyTests(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_waits_for_inflight_apply_and_stale_snapshots_cannot_capture_a_reused_name(bool active)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        var buckets = fixture.Database.GetCollection<SeaweedFSBucket>("SeaweedFSBuckets");
        var now = DateTime.UtcNow;
        var stale = new SeaweedFSBucket(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "app-assets",
            ObjectId.GenerateNewId().ToString(),
            1,
            active ? new ActiveSeaweedFSLifecycle() : new DeletingSeaweedFSLifecycle(now, now),
            true);
        await buckets.InsertOneAsync(stale, cancellationToken: ct);
        using var handler = new BucketApi();
        using var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://kubernetes.test" },
            handler);
        var options = new SeaweedFSClusterManifestOptions(
            "seaweed",
            "cell",
            SeaweedFSMode.SingleNode,
            10,
            null!,
            null!,
            new WorkloadIdentityLabels(
                stale.TenantId,
                "group",
                "scope",
                "cell",
                "target",
                "SeaweedFS",
                stale.SeaweedFSId,
                "deployment"),
            "bootstrap",
            "filer",
            "metadata");
        var objects = new SeaweedFSCustomObjects(client, options.Namespace, options.Identity.ToDictionary(), 1);
        var first = Reconciler();
        var second = Reconciler();
        var inflight = first.ApplyAsync(objects, options, stale, ct);
        try
        {
            await handler.Started.Task.WaitAsync(ct);
            var purging = stale with { Revision = 2, Lifecycle = new PurgingSeaweedFSLifecycle(now, now) };
            await buckets.ReplaceOneAsync(x => x.Id == stale.Id, purging, cancellationToken: ct);
            var requests = handler.RequestCount;
            Assert.False(await second.PurgeAsync(objects, purging, ct).WaitAsync(TimeSpan.FromSeconds(2), ct));
            Assert.Equal(requests, handler.RequestCount);
        }
        finally
        {
            handler.Release.TrySetResult();
            await inflight.WaitAsync(ct);
        }

        // Each deletion remains pending until a later GET confirms finalizer completion.
        var purged = false;
        for (var attempt = 0; attempt < 6 && !purged; attempt++)
            purged = await second.PurgeAsync(objects, stale, ct);
        Assert.True(purged);
        Assert.Empty(handler.Resources);

        // The durable Purging intent fences a delayed apply even before finalization commits.
        var afterPurge = handler.RequestCount;
        Assert.True(await first.ApplyAsync(objects, options, stale, ct));
        Assert.Equal(afterPurge, handler.RequestCount);
        await buckets.ReplaceOneAsync(
            x => x.Id == stale.Id,
            stale with { Revision = 3, Lifecycle = new DeletedSeaweedFSLifecycle(now), ReservesName = false },
            cancellationToken: ct);
        var replacement = stale with
        {
            Id = ObjectId.GenerateNewId().ToString(), Lifecycle = new ActiveSeaweedFSLifecycle()
        };
        await buckets.InsertOneAsync(replacement, cancellationToken: ct);
        Assert.False(
            await second.ApplyAsync(objects, options, replacement, ct)); // Projection intentionally unavailable.
        var afterReuse = handler.RequestCount;
        Assert.True(await first.ApplyAsync(objects, options, stale, ct));
        Assert.Equal(afterReuse, handler.RequestCount);
        Assert.True(await first.PurgeAsync(objects, stale, ct));
        var bucket = Assert.Single(handler.Resources.Values.Where(x => (string?)x["kind"] == "Bucket"));
        Assert.Equal(SeaweedFSNames.Bucket(replacement.Id), (string?)bucket["metadata"]?["name"]);
        Assert.Equal(stale.Name, (string?)bucket["spec"]?["name"]);
        Assert.DoesNotContain(handler.Resources.Values, x => (string?)x["kind"] is "S3Policy" or "S3PolicyBinding");

        SeaweedFSBucketReconciler Reconciler() => new(
            (_, _) => Task.FromResult<EnsureManagedSecretProjectionOutcome>(new ManagedSecretUnavailable()),
            (_, _) => throw new InvalidOperationException("No key should be read without a projection."),
            Operations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_native_operation_releases_the_lock_on_its_acquiring_thread(bool cancelled)
    {
        var bucket = new SeaweedFSBucket(
            "bucket",
            "tenant",
            "service",
            "app-assets",
            "secret",
            1,
            new ActiveSeaweedFSLifecycle(),
            true);
        // The single-bucket read is deliberately absent; the operation still exercises release.
        var operations = new SeaweedFSBucketOperations(
            Storage(),
            (
                _, _,
                _, _) => Task.FromResult<SeaweedFSBucket?>(null));
        using var cancellation = new CancellationTokenSource();
        var exception = await Record.ExceptionAsync(() => operations.RunAsync(
            bucket,
            async _ =>
            {
                await Task.Yield();
                if (cancelled)
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                }

                throw new InvalidOperationException("Native failure");
            },
            cancellation.Token));
        Assert.NotNull(exception);
        if (cancelled)
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else
            Assert.IsType<InvalidOperationException>(exception);
        // A dedicated new worker cannot mask a leaked Mongo lock through thread-local nesting.
        await Task.Factory
            .StartNew(
                () =>
                {
                    using var connection = Storage().GetConnection();
                    using var bucketLock = connection.AcquireDistributedLock(
                        "pole:seaweedfs:bucket:tenant:service:bucket",
                        TimeSpan.Zero);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private SeaweedFSBucketOperations Operations()
    {
        var reads = new MongoSeaweedFSReads(
            fixture.Database.GetCollection<SeaweedFSService>("SeaweedFS"),
            fixture.Database.GetCollection<SeaweedFSBucket>("SeaweedFSBuckets"));
        return new SeaweedFSBucketOperations(Storage(), reads.BucketAsync);
    }

    private MongoStorage Storage() => new(
        fixture.Client,
        fixture.Database.DatabaseNamespace.DatabaseName,
        new MongoStorageOptions
        {
            Prefix = "seaweedfs-bucket-tests", CheckConnection = false,
            MigrationOptions = new MongoMigrationOptions
            {
                MigrationStrategy = new MigrateMongoMigrationStrategy(),
                BackupStrategy = new CollectionMongoBackupStrategy()
            }
        });

    private sealed class BucketApi : DelegatingHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, JsonObject> Resources { get; } = new();
        public int RequestCount => Volatile.Read(ref requestCount);
        private int requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref requestCount);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Resources.TryGetValue(path, out var current)
                    ? Response(request, HttpStatusCode.OK, current)
                    : Response(request, HttpStatusCode.NotFound, new JsonObject());
            if (request.Method == HttpMethod.Delete)
            {
                Resources.Remove(path);
                return Response(request, HttpStatusCode.OK, new JsonObject());
            }

            var document = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            if (Started.TrySetResult())
                await Release.Task.WaitAsync(ct);
            var metadata = document["metadata"]!.AsObject();
            metadata["uid"] = Guid.NewGuid().ToString();
            metadata["resourceVersion"] = "1";
            metadata["generation"] = 1;
            document["status"] = new JsonObject
            {
                ["observedGeneration"] = 1,
                ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = "True" })
            };
            Resources[request.Method == HttpMethod.Post ? path + "/" + (string)metadata["name"]! : path] = document;
            return Response(request, HttpStatusCode.OK, document);
        }

        private static HttpResponseMessage Response(
            HttpRequestMessage request,
            HttpStatusCode code,
            JsonObject document) => new(code)
        {
            RequestMessage = request,
            Content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
