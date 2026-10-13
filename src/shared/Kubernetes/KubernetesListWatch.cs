using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using k8s;
using k8s.Autorest;

namespace Pole.Features.Core.Kubernetes;

public sealed record KubernetesListPage<T>(
    IReadOnlyList<T> Items,
    string? ResourceVersion,
    string? ContinuationToken);
public enum KubernetesListWatchChangeKind { Listed, Ready, Changed, Unavailable }
public sealed record KubernetesListWatchChange<T>(
    KubernetesListWatchChangeKind Kind,
    IReadOnlyList<T>? Items = null,
    WatchEventType? Type = null,
    T? Item = default);

/// <summary>Independent, bounded LIST/WATCH state for one native kind and selector.</summary>
public static class KubernetesListWatch
{
    private enum Failure { Unavailable, Expired, Malformed }
    private sealed record MoveResult(
        bool Moved,
        Failure? Failure = null);
    private sealed record ListResult<T>(
        IReadOnlyList<T>? Items,
        string? Version,
        Failure? Failure = null);

    public static async IAsyncEnumerable<KubernetesListWatchChange<T>> WatchAsync<T>(
        Func<string?, CancellationToken, Task<KubernetesListPage<T>>> listPage,
        Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
        Func<T, string?> resourceVersion,
        TimeProvider clock,
        int maximumItems = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? version = null;
        var retry = TimeSpan.FromMilliseconds(500);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (version is null)
            {
                var listed = await ListAsync(listPage, maximumItems, cancellationToken);
                if (listed.Failure is not null)
                {
                    yield return new(KubernetesListWatchChangeKind.Unavailable);
                    await Task.Delay(retry, clock, cancellationToken);
                    retry = NextRetry(retry);
                    continue;
                }
                version = listed.Version!;
                yield return new(KubernetesListWatchChangeKind.Listed, listed.Items);
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Failure? callbackFailure = null;
            var openingState = 0; // 0 waiting, 1 opened, 2 expired. One winner prevents late false readiness.
            ITimer? watchdog = null;
            DateTimeOffset? openedAt = null;
            using var headers = KubernetesWatchHeaders.Begin(() =>
            {
                if (Interlocked.CompareExchange(ref openingState, 1, 0) != 0) return;
                watchdog!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                openedAt = clock.GetUtcNow();
                opened.TrySetResult();
            }, lifetime.Token);
            using var openingWatchdog = watchdog = clock.CreateTimer(_ =>
            {
                if (Interlocked.CompareExchange(ref openingState, 2, 0) == 0) lifetime.Cancel();
            }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            var events = watch(version, exception =>
            {
                callbackFailure ??= Classify(exception);
                // SDK onError otherwise waits for EOF, even after Status410 or malformed JSON.
                lifetime.Cancel();
            }, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
            Task<bool>? pending = null;
            Failure? failure = null;
            var ready = false;
            try
            {
                pending = events.MoveNextAsync().AsTask();
                if (await Task.WhenAny(opened.Task, pending) == opened.Task && opened.Task.IsCompletedSuccessfully && !lifetime.IsCancellationRequested)
                {
                    ready = true;
                    yield return new(KubernetesListWatchChangeKind.Ready);
                }
                while (!cancellationToken.IsCancellationRequested)
                {
                    var moved = await MoveAsync(pending);
                    pending = null;
                    failure = callbackFailure ?? moved.Failure;
                    if (failure is not null || !moved.Moved) break;
                    if (!ready && opened.Task.IsCompletedSuccessfully && !lifetime.IsCancellationRequested)
                    {
                        ready = true;
                        yield return new(KubernetesListWatchChangeKind.Ready);
                    }
                    var (type, item) = events.Current;
                    var nextVersion = resourceVersion(item);
                    if (string.IsNullOrEmpty(nextVersion)) { failure = Failure.Malformed; break; }
                    retry = TimeSpan.FromMilliseconds(500);
                    version = nextVersion; // Opaque, local to this kind AND selector; never order versions.
                    if (type != WatchEventType.Bookmark)
                        yield return new(KubernetesListWatchChangeKind.Changed, Type: type, Item: item);
                    pending = events.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                lifetime.Cancel();
                headers.CloseResponse();
                if (pending is not null) await MoveAsync(pending);
                await events.DisposeAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();
            // A quiet, functioning transport can expire without a bookmark or data frame.
            if (openedAt is { } since && clock.GetUtcNow() - since >= TimeSpan.FromSeconds(30))
                retry = TimeSpan.FromMilliseconds(500);
            yield return new(KubernetesListWatchChangeKind.Unavailable);
            if (failure is Failure.Expired or Failure.Malformed) version = null;
            await Task.Delay(retry, clock, cancellationToken);
            retry = NextRetry(retry);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TimeSpan NextRetry(TimeSpan retry) => TimeSpan.FromMilliseconds(Math.Min(retry.TotalMilliseconds * 2, 30_000));

    private static async Task<MoveResult> MoveAsync(Task<bool> pending)
    {
        try { return new(await pending); }
        catch (Exception exception) when (IsNativeFailure(exception)) { return new(false, Classify(exception)); }
    }

    private static async Task<ListResult<T>> ListAsync<T>(Func<string?, CancellationToken, Task<KubernetesListPage<T>>> page, int maximumItems, CancellationToken ct)
    {
        try
        {
            var items = new List<T>();
            string? continuation = null;
            string? version = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var listed = await page(continuation, ct);
                if (string.IsNullOrEmpty(listed.ResourceVersion) || version is not null && listed.ResourceVersion != version)
                    return new(null, null, Failure.Malformed);
                version = listed.ResourceVersion;
                items.AddRange(listed.Items);
                if (items.Count > maximumItems) return new(null, null, Failure.Unavailable);
                continuation = listed.ContinuationToken;
                if (!string.IsNullOrEmpty(continuation) && (!seen.Add(continuation) || seen.Count > 100))
                    return new(null, null, Failure.Malformed);
            } while (!string.IsNullOrEmpty(continuation));
            return new(items, version);
        }
        catch (Exception exception) when (IsNativeFailure(exception) && !ct.IsCancellationRequested)
        { return new(null, null, Classify(exception)); }
    }

    private static bool IsNativeFailure(Exception exception) => exception is HttpOperationException or KubernetesException or
        HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException;

    private static Failure Classify(Exception exception) => exception switch
    {
        HttpOperationException { Response.StatusCode: HttpStatusCode.Gone } => Failure.Expired,
        KubernetesException { Status.Code: 410 } => Failure.Expired,
        JsonException => Failure.Malformed,
        _ => Failure.Unavailable
    };
}
