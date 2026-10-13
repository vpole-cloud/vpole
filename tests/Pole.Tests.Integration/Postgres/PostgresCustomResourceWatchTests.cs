using System.Runtime.CompilerServices;
using System.Text.Json;
using k8s;
using Pole.Features.Core.Kubernetes;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

/// <summary>Proof of the installed SDK custom-resource seam before provider parser/source expansion.</summary>
public sealed class PostgresCustomResourceWatchTests
{
    [Fact]
    public async Task Actual_object_valued_custom_watch_is_quiet_ready_and_preserves_owned_selectors_and_opaque_RV()
    {
        using var stream = new NativeWatchTestStream();
        using var handler = new NativeWatchTestHandler(_ => List("collection/opaque"), _ => stream);
        using var client = KubernetesNativeWatchTests.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(KubernetesListWatchChangeKind.Listed, changes.Current.Kind);
        Assert.IsType<JsonElement>(Assert.Single(changes.Current.Items!));
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        Assert.Equal(0, stream.FramesWritten);
        var uri = Assert.Single(handler.Requests, KubernetesNativeWatchTests.IsWatch);
        Assert.Equal("/apis/postgresql.cnpg.io/v1/namespaces/owned/clusters", uri.AbsolutePath);
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("fieldSelector=metadata.name=pg-owned", query);
        Assert.Contains("labelSelector=pole.io/tenant-id=tenant", query);
        Assert.Contains("resourceVersion=collection/opaque", query);
        Assert.Contains("allowWatchBookmarks=true", query);
        stream.Write("{\"type\":\"MODIFIED\",\"object\":{\"apiVersion\":\"postgresql.cnpg.io/v1\",\"kind\":\"Cluster\",\"metadata\":{\"name\":\"pg-owned\",\"uid\":\"controller\",\"resourceVersion\":\"item/opaque\"}}}\n");
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Changed, changes.Current.Kind);
        Assert.Equal("controller", changes.Current.Item.GetProperty("metadata").GetProperty("uid").GetString());
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.True(stream.Disposed);
        // The transport owns its response, never the cached target client.
        await client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>("postgresql.cnpg.io", "v1", "owned", "clusters");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_Status410_or_malformed_body_cancels_without_EOF_and_restarts_LIST(bool malformed)
    {
        using var first = new NativeWatchTestStream();
        using var second = new NativeWatchTestStream();
        var watches = 0;
        using var handler = new NativeWatchTestHandler(_ => List("collection"), _ => ++watches == 1 ? first : second);
        using var client = KubernetesNativeWatchTests.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, cancellation.Token).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        await changes.MoveNextAsync();
        first.Write(malformed ? "{invalid-json\n" : "{\"type\":\"ERROR\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"code\":410,\"message\":\"native-canary-secret\"}}\n");
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        Assert.True(first.Disposed);
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Listed, changes.Current.Kind);
        Assert.Equal(2, handler.Requests.Count(uri => !KubernetesNativeWatchTests.IsWatch(uri)));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Metadata_only_custom_BOOKMARK_resumes_after_EOF_without_parsing_ordinary_cluster_spec()
    {
        using var first = new NativeWatchTestStream();
        using var second = new NativeWatchTestStream();
        var watches = 0;
        using var handler = new NativeWatchTestHandler(_ => List("collection"), _ => ++watches == 1 ? first : second);
        using var client = KubernetesNativeWatchTests.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, cancellation.Token).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        await changes.MoveNextAsync();
        first.Write("{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"bookmark/opaque\"}}}\n");
        first.Complete();
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        var uris = handler.Requests.Where(KubernetesNativeWatchTests.IsWatch).ToArray();
        Assert.Contains("resourceVersion=bookmark/opaque", Uri.UnescapeDataString(uris[1].Query));
        Assert.Single(handler.Requests, uri => !KubernetesNativeWatchTests.IsWatch(uri));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    private static IAsyncEnumerable<KubernetesListWatchChange<JsonElement>> Changes(IKubernetes client, CancellationToken ct) =>
        KubernetesListWatch.WatchAsync<JsonElement>(async (continuation, token) =>
        {
            var page = await client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>("postgresql.cnpg.io", "v1", "owned", "clusters",
                continueParameter: continuation, fieldSelector: "metadata.name=pg-owned", labelSelector: "pole.io/tenant-id=tenant", limit: 100, cancellationToken: token);
            var metadata = page.GetProperty("metadata");
            return new(page.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray(),
                metadata.GetProperty("resourceVersion").GetString(), metadata.TryGetProperty("continue", out var next) ? next.GetString() : null);
        }, (version, error, token) => WatchAsync(client, version, error, token),
            item => item.GetProperty("metadata").GetProperty("resourceVersion").GetString(), TimeProvider.System, cancellationToken: ct);

    private static async IAsyncEnumerable<(WatchEventType Type, JsonElement Item)> WatchAsync(
        IKubernetes client, string version, Action<Exception> error, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var (type, item) in client.CustomObjects.WatchListNamespacedCustomObjectAsync("postgresql.cnpg.io", "v1", "owned", "clusters",
            allowWatchBookmarks: true, fieldSelector: "metadata.name=pg-owned", labelSelector: "pole.io/tenant-id=tenant", resourceVersion: version,
            timeoutSeconds: 300, onError: error, cancellationToken: ct))
            yield return (type, Assert.IsType<JsonElement>(item));
    }
    private static string List(string version) => JsonSerializer.Serialize(new
    {
        apiVersion = "postgresql.cnpg.io/v1",
        kind = "ClusterList",
        metadata = new { resourceVersion = version },
        items = new[] { new { apiVersion = "postgresql.cnpg.io/v1", kind = "Cluster", metadata = new { name = "pg-owned", uid = "controller", resourceVersion = "item" } } }
    });
}
