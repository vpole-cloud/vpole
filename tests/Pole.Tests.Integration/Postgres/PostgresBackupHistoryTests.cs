using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using Pole.Features.Backups.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Backups;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresBackupHistoryTests
{
    private static readonly BackupArchiveContext Archive = new(
        new("archive", "server", new("https://s3.invalid", "bucket", "p")),
        new("tenant", "pole/postgresql", "service"),
        new("17", 10, 1, 1),
        new("group", "scope", "target", "cell", "ns", "deployment", "storage", 1, "compute", 1));

    [Fact]
    public async Task Initial_snapshot_is_one_page_and_a_restarted_provider_resumes_its_opaque_continuation()
    {
        using var stream = new NativeWatchTestStream();
        using var handler = Handler(
            request => request.RequestUri!.Query.Contains("continue=")
                ? List("snapshot/opaque", null, Backup("owned", "publisher", "completed"))
                : List(
                    "snapshot/opaque",
                    "next/page",
                    Enumerable.Range(0, 200)
                        .Select(index => Backup("foreign-" + index, "another-publisher", "running"))
                        .ToArray()),
            stream);
        using var client = NativeWatchTestClient.Create(handler);
        var first = Assert.IsType<BackupHistoryBatch>(
            (await Provider(client).ReadHistoryAsync(Request(new BeginNativeBackupHistory()), default)).Value);
        Assert.Equal(200, first.ConsumedCount);
        Assert.Empty(first.Changes);
        var cursor = Assert.IsType<PageNativeBackupHistory>(first.Position);
        Assert.Equal("snapshot/opaque", cursor.Stream.Version);
        Assert.Equal("next/page", cursor.Continuation);
        var second = Assert.IsType<BackupHistoryBatch>(
            (await Provider(client).ReadHistoryAsync(Request(cursor), default)).Value);
        var run = Assert.Single(second.Changes);
        Assert.Equal("copy-owned", Assert.IsType<NativeScheduledBackupCompleted>(run.State).BackupId);
        Assert.Equal("snapshot/opaque", Assert.IsType<FollowNativeBackupHistory>(second.Position).Stream.Version);
        var lists = handler.Requests.Where(uri => uri.AbsolutePath.EndsWith("/backups")).ToArray();
        Assert.Equal(2, lists.Length);
        Assert.All(lists, uri => Assert.Contains("limit=200", Uri.UnescapeDataString(uri.Query)));
        Assert.Contains("continue=next/page", Uri.UnescapeDataString(lists[1].Query));
        Assert.DoesNotContain(handler.Requests, NativeWatchTestClient.IsWatch);
    }

    [Fact]
    public async Task Watch_consumes_at_most_200_events_including_foreign_objects_and_resumes_without_LIST()
    {
        using var stream = new NativeWatchTestStream();
        for (var index = 0; index < 201; index++)
            stream.Write(Frame("MODIFIED", Backup("event-" + index, index == 5 ? "publisher" : "foreign", "running")));
        using var handler = Handler(
            _ => throw new InvalidOperationException("A following checkpoint must not LIST."),
            stream);
        using var client = NativeWatchTestClient.Create(handler);
        var result = Assert.IsType<BackupHistoryBatch>(
            (await Provider(client)
                .ReadHistoryAsync(Request(new FollowNativeBackupHistory(new("publisher", "starting/opaque"))), default))
            .Value);
        Assert.Equal(200, result.ConsumedCount);
        Assert.Equal("event-5", Assert.Single(result.Changes).NativeUid);
        Assert.True(Assert.Single(result.Activities).InUse);
        Assert.Equal("rv-event-199", Assert.IsType<FollowNativeBackupHistory>(result.Position).Stream.Version);
        Assert.True(stream.Disposed);
        var uri = Assert.Single(handler.Requests, NativeWatchTestClient.IsWatch);
        Assert.Contains("resourceVersion=starting/opaque", Uri.UnescapeDataString(uri.Query));
        Assert.Contains("allowWatchBookmarks=true", Uri.UnescapeDataString(uri.Query));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_malformed_watch_is_closed_without_waiting_for_EOF(bool malformed)
    {
        using var stream = new NativeWatchTestStream();
        stream.Write(
            malformed
                ? "{broken-json\n"
                : "{\"type\":\"ERROR\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"code\":410,\"message\":\"native-canary\"}}\n");
        using var handler = Handler(_ => throw new InvalidOperationException("No LIST in this step."), stream);
        using var client = NativeWatchTestClient.Create(handler);
        var result = await Provider(client)
            .ReadHistoryAsync(Request(new FollowNativeBackupHistory(new("publisher", "old"))), default)
            .WaitAsync(TimeSpan.FromSeconds(3));
        if (malformed)
            Assert.IsType<BackupHistoryUnreadable>(result.Value);
        else
            Assert.IsType<BackupHistoryRestartRequired>(result.Value);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Quiet_watch_deadline_preserves_a_bookmark_for_the_next_step()
    {
        using var stream = new NativeWatchTestStream();
        stream.Write("{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"bookmark/opaque\"}}}\n");
        using var handler = Handler(_ => throw new InvalidOperationException("No LIST in this step."), stream);
        using var client = NativeWatchTestClient.Create(handler);
        var result = Assert.IsType<BackupHistoryBatch>(
            (await Provider(client)
                .ReadHistoryAsync(Request(new FollowNativeBackupHistory(new("publisher", "old"))), default)
                .WaitAsync(TimeSpan.FromSeconds(18))).Value);
        Assert.Equal(1, result.ConsumedCount);
        Assert.Empty(result.Changes);
        Assert.Equal("bookmark/opaque", Assert.IsType<FollowNativeBackupHistory>(result.Position).Stream.Version);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("publisher", "different", typeof(BackupHistorySuspensionChanged))]
    [InlineData("replaced", "publisher", typeof(BackupHistoryRestartRequired))]
    [InlineData("publisher", "publisher", typeof(BackupHistoryBatch))]
    public async Task A_drain_verifies_the_exact_paused_schedule_publisher_and_revision(
        string previousPublisher,
        string fencePublisher,
        Type expected)
    {
        using var stream = new NativeWatchTestStream();
        using var handler = Handler(_ => List("snapshot", null), stream, suspended: true);
        using var client = NativeWatchTestClient.Create(handler);
        var request = Request(new PageNativeBackupHistory(new(previousPublisher, "snapshot"), "next")) with
        {
            Mode = new SuspendedNativeBackupHistory(new("policy", 2, fencePublisher))
        };
        var result = await Provider(client).ReadHistoryAsync(request, default);
        Assert.IsType(expected, result.Value);
    }

    [Fact]
    public async Task Caller_cancellation_remains_on_the_standard_exception_path()
    {
        using var stream = new NativeWatchTestStream();
        using var handler = Handler(_ => List("snapshot", null), stream);
        using var client = NativeWatchTestClient.Create(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(client).ReadHistoryAsync(Request(new BeginNativeBackupHistory()), new CancellationToken(true)));
    }

    private static ReadBackupHistoryRequest Request(NativeBackupHistoryPosition position) => new(
        "policy",
        Archive,
        new LiveNativeBackupHistory(),
        position);

    private static NativeWatchTestHandler Handler(
        Func<HttpRequestMessage, string> backups,
        NativeWatchTestStream stream,
        bool suspended = false) => new(
        request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/backups"))
                return backups(request);
            var native = request.RequestUri.AbsolutePath.Contains("/scheduledbackups/")
                ? PostgresBackupManifests.schedule(Archive, new("0 2 * * *", suspended, 7), "source")
                : JsonNode.Parse("{\"metadata\":{\"labels\":{}}}")!;
            foreach (var pair in PostgresBackupManifests.sourceLabels(Archive))
                native["metadata"]!["labels"]![pair.Item1] = pair.Item2;
            native["metadata"]!["uid"]
                = request.RequestUri.AbsolutePath.Contains("/scheduledbackups/") ? "publisher" : "source";
            native["metadata"]!["resourceVersion"] = "native-version";
            native["metadata"]!["annotations"] = new JsonObject
            {
                ["backups.pole.io/policy-id"] = "policy", ["backups.pole.io/policy-revision"] = "2",
                ["backups.pole.io/retired"] = suspended ? "true" : "false"
            };
            return native.ToJsonString();
        },
        _ => stream);

    private static JsonNode Backup(
        string uid,
        string owner,
        string phase) => JsonSerializer.SerializeToNode(
        new
        {
            apiVersion = "postgresql.cnpg.io/v1", kind = "Backup",
            metadata = new
            {
                uid, name = uid, resourceVersion = "rv-" + uid, creationTimestamp = "2026-11-13T12:00:00Z",
                ownerReferences = new[] { new { kind = "ScheduledBackup", uid = owner } }
            },
            spec = new { cluster = new { name = "pg-service" } },
            status = new { phase, backupId = "copy-" + uid, stoppedAt = "2026-11-13T12:01:00Z" }
        })!;

    private static string List(
        string version,
        string? continuation,
        params JsonNode[] items) => JsonSerializer.Serialize(
        new { metadata = new { resourceVersion = version, @continue = continuation }, items });

    private static string Frame(string type, JsonNode native) =>
        JsonSerializer.Serialize(new { type, @object = native }) + "\n";

    private static IBackupProvider Provider(IKubernetes client) =>
        new PostgresBackupProvider(new Clients(client), null!, null!);

    private sealed class Clients(
        IKubernetes client
    ) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string target, CancellationToken ct = default) => Task.FromResult(client);

        public Task<IKubernetes> GetForCleanupAsync(string target, CancellationToken ct = default) =>
            GetAsync(target, ct);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
