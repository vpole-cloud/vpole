using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class KubernetesNativeWatchTests
{
    [Fact]
    public async Task Quiet_actual_SDK_watch_is_ready_before_first_frame_and_cancels_only_its_response()
    {
        using var stream = new NativeWatchTestStream();
        using var handler = new NativeWatchTestHandler(_ => List("opaque-list"), _ => stream);
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, "first", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(KubernetesListWatchChangeKind.Listed, changes.Current.Kind);
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        Assert.Equal(0, stream.FramesWritten);
        cancellation.Cancel();
        await changes.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stream.Disposed);
        // Neither the cached client nor a nonwatch request is disposed/marked ready.
        var opened = 0;
        using (KubernetesWatchHeaders.Begin(() => opened++, CancellationToken.None))
            await client.AppsV1.ListNamespacedDeploymentAsync("other");
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Concurrent_watch_registrations_on_one_client_do_not_steal_readiness_or_cancellation()
    {
        using var first = new NativeWatchTestStream();
        using var second = new NativeWatchTestStream();
        using var handler = new NativeWatchTestHandler(_ => List("list"),
            request =>
                request.RequestUri!.AbsolutePath.Contains("/first/", StringComparison.Ordinal) ? first : second);
        using var client = Client(handler);
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a = Changes(client, "first", firstCancellation.Token).GetAsyncEnumerator();
        var b = Changes(client, "second", secondCancellation.Token).GetAsyncEnumerator();
        await Task.WhenAll(a.MoveNextAsync().AsTask(), b.MoveNextAsync().AsTask());
        await Task.WhenAll(a.MoveNextAsync().AsTask(), b.MoveNextAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, a.Current.Kind);
        Assert.Equal(KubernetesListWatchChangeKind.Ready, b.Current.Kind);
        firstCancellation.Cancel();
        await a.DisposeAsync();
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        second.Write(Frame("MODIFIED", "second-change"));
        Assert.True(await b.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("second-change", b.Current.Item!.Metadata.ResourceVersion);
        secondCancellation.Cancel();
        await b.DisposeAsync();
        Assert.True(second.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_band_410_or_malformed_JSON_stops_open_stream_without_waiting_for_EOF_and_relists(
        bool malformed)
    {
        using var bad = new NativeWatchTestStream();
        using var healthy = new NativeWatchTestStream();
        var lists = 0;
        var watches = 0;
        using var handler = new NativeWatchTestHandler(_ => List($"list-{Interlocked.Increment(ref lists)}"),
            _ => Interlocked.Increment(ref watches) == 1 ? bad : healthy);
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, "first", cancellation.Token).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        await changes.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        bad.Write(malformed
            ? "{invalid-json\n"
            : "{\"type\":\"ERROR\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"code\":410,\"message\":\"secret-native-canary\"}}\n");
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        Assert.True(bad.Disposed); // No server EOF was supplied.
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Listed, changes.Current.Kind);
        Assert.Equal(2, lists);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Consistent_pagination_accepts_empty_pages_and_bookmark_resumes_opaque_version_after_EOF()
    {
        using var first = new NativeWatchTestStream();
        using var second = new NativeWatchTestStream();
        var watches = 0;
        using var handler = new NativeWatchTestHandler(request =>
                request.RequestUri!.Query.Contains("continue=", StringComparison.Ordinal)
                    ? List("opaque/collection",
                        null,
                        new V1Deployment { Metadata = new V1ObjectMeta { ResourceVersion = "item-version" } })
                    : List("opaque/collection", "next-page"),
            _ => Interlocked.Increment(ref watches) == 1 ? first : second);
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, "first", cancellation.Token).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        Assert.Single(changes.Current.Items!);
        await changes.MoveNextAsync();
        first.Write(Frame("BOOKMARK", "opaque/bookmark"));
        first.Complete();
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable,
            changes.Current.Kind); // Bookmark is not an invalidation.
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        var watchUris = handler.Requests.Where(IsWatch).ToArray();
        Assert.Contains("resourceVersion=opaque/bookmark", Uri.UnescapeDataString(watchUris[1].Query));
        Assert.Equal(2, handler.Requests.Count(uri => !IsWatch(uri)));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Inconsistent_page_versions_do_not_open_a_watch_or_claim_coverage()
    {
        using var handler = new NativeWatchTestHandler(request =>
                request.RequestUri!.Query.Contains("continue=", StringComparison.Ordinal)
                    ? List("different")
                    : List("first", "next"),
            _ => throw new InvalidOperationException("Incomplete lists cannot watch."));
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, "first", cancellation.Token).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        Assert.DoesNotContain(handler.Requests, IsWatch);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Opening_watchdog_cancels_after15s_without_headers_and_does_not_cancel_an_open_quiet_watch()
    {
        var clock = new NativeWatchTestClock();
        using var stalled = new NoWatchHeadersHandler();
        using var stalledClient = Client(stalled);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(stalledClient, "first", cancellation.Token, clock).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        var pending = changes.MoveNextAsync().AsTask();
        await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        // The SDK can finish its enumerator before the cancelled HTTP handler unwinds.
        Assert.True(await stalled.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await changes.DisposeAsync();

        using var stream = new NativeWatchTestStream();
        using var handler = new NativeWatchTestHandler(_ => List("list"), _ => stream);
        using var client = Client(handler);
        using var openCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var open = Changes(client, "second", openCancellation.Token, clock).GetAsyncEnumerator();
        await open.MoveNextAsync();
        await open.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Ready, open.Current.Kind);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(stream.Disposed);
        stream.Write(Frame("MODIFIED", "after-deadline"));
        await open.MoveNextAsync();
        Assert.Equal("after-deadline", open.Current.Item!.Metadata.ResourceVersion);
        openCancellation.Cancel();
        await open.DisposeAsync();
    }

    [Fact]
    public async Task Healthy_idle_watch_expiry_reconnects_promptly_without_bookmark_or_first_frame()
    {
        var clock = new NativeWatchTestClock();
        using var first = new NativeWatchTestStream();
        using var second = new NativeWatchTestStream();
        using var third = new NativeWatchTestStream();
        var opened = 0;
        using var handler = new NativeWatchTestHandler(_ => List("list"),
            _ => ++opened switch { 1 => first, 2 => second, _ => third });
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Changes(client, "first", cancellation.Token, clock).GetAsyncEnumerator();
        await changes.MoveNextAsync();
        await changes.MoveNextAsync();
        first.Complete(); // Immediate EOF grows retry; bare200 does not reset it.
        await changes.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        var reconnect = changes.MoveNextAsync().AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        clock.Advance(TimeSpan.FromSeconds(30));
        second.Complete(); // Healthy idle expiry has no first native frame or discretionary bookmark.
        await changes.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, changes.Current.Kind);
        reconnect = changes.MoveNextAsync().AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(KubernetesListWatchChangeKind.Ready, changes.Current.Kind);
        Assert.Equal(3, opened);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Original_content_disposal_fault_still_disposes_response_wrapper_and_request()
    {
        var original = new TrackingContent(throws: true);
        var wrapper = new TrackingContent();
        var requestContent = new TrackingContent();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://native-watch.test/watch?watch=true")
        {
            Content = requestContent
        };
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = original,
            RequestMessage = request
        };
        using var client = new HttpClient(new KubernetesWatchHeaders
        {
            InnerHandler = new FixedResponseHandler(response)
        });
        using var registration = KubernetesWatchHeaders.Begin(() =>
            {
            },
            CancellationToken.None);
        await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.Content = wrapper; // Equivalent ownership seam to the SDK's replacement wrapper.
        Assert.Throws<InvalidOperationException>(registration.CloseResponse);
        Assert.True(original.Disposed);
        Assert.True(wrapper.Disposed);
        Assert.True(requestContent.Disposed);
    }

    private sealed class TrackingContent(bool throws = false) : StringContent("{}")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (throws)
                throw new InvalidOperationException("Expected disposal fault.");
        }
    }

    private sealed class FixedResponseHandler(HttpResponseMessage response) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class NoWatchHeadersHandler : DelegatingHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (IsWatch(request.RequestUri!))
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    CancellationObserved.TrySetResult(cancellationToken.IsCancellationRequested);
                }
            }

            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(List("list"))
            };
        }
    }

    internal static k8s.Kubernetes Client(DelegatingHandler terminal) => new(
        new KubernetesClientConfiguration { Host = "http://native-watch.test" },
        new KubernetesWatchHeaders(),
        terminal);

    internal static bool IsWatch(Uri uri) => uri.Query.Contains("watch=true", StringComparison.OrdinalIgnoreCase);

    internal static string List(string version, string? continuation = null, params V1Deployment[] items) =>
        KubernetesJson.Serialize(new V1DeploymentList
        {
            ApiVersion = "apps/v1",
            Kind = "DeploymentList",
            Metadata = new V1ListMeta { ResourceVersion = version, ContinueProperty = continuation },
            Items = items
        });

    internal static string Frame(string type, string version) =>
        $"{{\"type\":\"{type}\",\"object\":{{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\",\"metadata\":{{\"resourceVersion\":\"{version}\"}}}}}}\n";

    private static IAsyncEnumerable<KubernetesListWatchChange<V1Deployment>> Changes(IKubernetes client,
        string namespaceName,
        CancellationToken ct,
        TimeProvider? clock = null) =>
        KubernetesListWatch.WatchAsync<V1Deployment>(async (continuation, token) =>
            {
                var page = await client.AppsV1.ListNamespacedDeploymentAsync(namespaceName,
                    continueParameter: continuation,
                    limit: 100,
                    cancellationToken: token);
                return new(page.Items.ToArray(), page.Metadata.ResourceVersion, page.Metadata.ContinueProperty);
            },
            (version, error, token) => client.AppsV1.WatchListNamespacedDeploymentAsync(namespaceName,
                allowWatchBookmarks: true,
                resourceVersion: version,
                onError: error,
                cancellationToken: token),
            item => item.Metadata.ResourceVersion,
            clock ?? TimeProvider.System,
            cancellationToken: ct);
}

internal sealed class NativeWatchTestHandler(
    Func<HttpRequestMessage, string> list,
    Func<HttpRequestMessage, NativeWatchTestStream> watch) : DelegatingHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = KubernetesNativeWatchTests.IsWatch(request.RequestUri!)
                ? new StreamContent(watch(request))
                : new StringContent(list(request), Encoding.UTF8, "application/json")
        });
    }
}

internal sealed class NativeWatchTestStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _position;

    public int FramesWritten { get; private set; }

    public bool Disposed { get; private set; }

    private readonly TaskCompletionSource _nextReadAfterFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task NextReadAfterFrame => _nextReadAfterFrame.Task;

    public void Write(string frame)
    {
        FramesWritten++;
        _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(frame));
    }

    public void Complete() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _position == _current.Length)
        {
            if (_current is not null)
                _nextReadAfterFrame.TrySetResult();
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            if (!_chunks.Reader.TryRead(out _current))
                continue;
            _position = 0;
        }

        var length = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        Complete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class NativeWatchTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToList();
            foreach (var timer in due)
                timer.Due = DateTimeOffset.MaxValue;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(NativeWatchTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
                Due = DateTimeOffset.MaxValue;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
