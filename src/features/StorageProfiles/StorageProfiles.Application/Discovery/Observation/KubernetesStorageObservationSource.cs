using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Persistence.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery.Observation;

public sealed class KubernetesStorageObservationSource(
    StorageObservationNativeReader native,
    StorageObservationOptions options,
    TimeProvider clock) : IStorageObservationSource
{
    public async IAsyncEnumerable<StorageObservationChange> WatchAsync(StorageObservationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var session = new SourceSession(native, context, options, clock, cancellationToken);
        await foreach (var change in session.RunAsync(cancellationToken))
            yield return change;
    }

    private abstract record NativeItem(
        string Uid,
        string Key);

    private sealed record ClassItem(
        string Uid,
        string Key,
        StorageClassObservation Value) : NativeItem(Uid, Key);

    private sealed record VolumeItem(
        string Uid,
        string Key,
        PersistentVolumeObservation Value) : NativeItem(Uid, Key);

    private sealed record ClaimItem(
        string Uid,
        string Key,
        PersistentVolumeClaimObservation Value) : NativeItem(Uid, Key);

    private sealed record NativeMessage(
        string Stream,
        KubernetesListWatchChangeKind Kind,
        NativeItem[]? Items = null,
        WatchEventType? Type = null,
        NativeItem? Item = null);

    private sealed class StreamState
    {
        public bool Listed { get; set; }

        public bool Ready { get; set; }

        public Dictionary<string, NativeItem> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SourceUnavailableException : Exception;

    private sealed class SourceSession : IAsyncDisposable
    {
        private readonly StorageObservationNativeReader native;
        private readonly StorageObservationContext context;
        private readonly StorageObservationOptions options;
        private readonly TimeProvider clock;
        private readonly CancellationTokenSource lifetime;

        private readonly Channel<NativeMessage> messages = Channel.CreateBounded<NativeMessage>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        private readonly Dictionary<string, StreamState> streams = new(StringComparer.Ordinal)
        {
            ["classes"] = new(),
            ["volumes"] = new(),
            ["claims"] = new()
        };

        private readonly List<Task> pumps = [];
        private string? sourceId;
        private bool covered;

        internal SourceSession(StorageObservationNativeReader native,
            StorageObservationContext context,
            StorageObservationOptions options,
            TimeProvider clock,
            CancellationToken ct)
        {
            this.native = native;
            this.context = context;
            this.options = options;
            this.clock = clock;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        internal async IAsyncEnumerable<StorageObservationChange> RunAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            Start();
            while (await messages.Reader.WaitToReadAsync(ct))
            {
                while (messages.Reader.TryRead(out var message))
                {
                    var state = streams[message.Stream];
                    if (message.Kind == KubernetesListWatchChangeKind.Unavailable)
                    {
                        state.Ready = false;
                        covered = false;
                        sourceId = null;
                        yield return new(StorageObservationChangeKind.Unavailable);
                        continue;
                    }

                    var membershipChanged = false;
                    if (message.Kind == KubernetesListWatchChangeKind.Listed)
                    {
                        membershipChanged = !state.Items.Keys.ToHashSet(StringComparer.Ordinal)
                            .SetEquals(message.Items!.Select(item => item.Uid));
                        state.Items.Clear();
                        foreach (var item in message.Items!)
                        {
                            if (!state.Items.TryAdd(item.Uid, item))
                                throw new SourceUnavailableException();
                        }

                        state.Listed = true;
                    }
                    else if (message.Kind == KubernetesListWatchChangeKind.Changed && message.Item is { } item)
                    {
                        if (message.Type == WatchEventType.Deleted)
                            membershipChanged = state.Items.Remove(item.Uid);
                        else
                        {
                            membershipChanged = !state.Items.ContainsKey(item.Uid);
                            foreach (var old in state.Items.Values
                                         .Where(old => old.Key == item.Key && old.Uid != item.Uid).ToArray())
                            {
                                state.Items.Remove(old.Uid);
                                membershipChanged = true;
                            }

                            if (state.Items.Count >= options.MaximumItemsPerKind && !state.Items.ContainsKey(item.Uid))
                            {
                                covered = false;
                                sourceId = null;
                                lifetime.Cancel();
                                yield return new(StorageObservationChangeKind.Unavailable);
                                throw new SourceUnavailableException();
                            }

                            state.Items[item.Uid] = item;
                        }
                    }
                    else if (message.Kind == KubernetesListWatchChangeKind.Ready)
                        state.Ready = true;

                    if (covered && membershipChanged)
                    {
                        covered = false;
                        sourceId = null;
                        // Fence old cuts before any replacement state is accepted.
                        yield return new(StorageObservationChangeKind.Unavailable);
                    }

                    if (!streams.Values.All(stream => stream.Listed && stream.Ready))
                        continue;
                    var inventory = Cut();
                    if (inventory is null)
                    {
                        covered = false;
                        sourceId = null;
                        lifetime.Cancel();
                        yield return new(StorageObservationChangeKind.Unavailable);
                        throw new SourceUnavailableException();
                    }

                    if (!covered)
                    {
                        covered = true;
                        sourceId = Guid.NewGuid().ToString("N");
                        yield return new(StorageObservationChangeKind.Ready, sourceId, inventory);
                    }
                    else if (message.Kind is KubernetesListWatchChangeKind.Listed
                             or KubernetesListWatchChangeKind.Changed)
                        yield return new(StorageObservationChangeKind.Changed, sourceId, inventory);
                }
            }
        }

        private StorageInventoryObservation? Cut() => StorageObservationValueMappings.Sanitize(new(
                streams["classes"].Items.Values.Cast<ClassItem>().Select(item => item.Value).ToArray(),
                streams["volumes"].Items.Values.Cast<VolumeItem>().Select(item => item.Value).ToArray(),
                streams["claims"].Items.Values.Cast<ClaimItem>().Select(item => item.Value).ToArray(),
                true),
            options);

        private void Start()
        {
            Add<V1StorageClass>("classes",
                async (next, ct) =>
                {
                    var page = await native.ReadNativeAsync(context,
                        (client, token) =>
                            client.StorageV1.ListStorageClassAsync(limit: 100,
                                continueParameter: next,
                                cancellationToken: token),
                        ct);
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.StorageV1.WatchListStorageClassAsync(resourceVersion: version,
                        allowWatchBookmarks: true,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => new ClassItem(Uid(item.Metadata),
                    item.Metadata.Name,
                    StorageObservationValueMappings.From(KubernetesStorageDiscovery.MapStorageClass(item))));
            Add<V1PersistentVolume>("volumes",
                async (next, ct) =>
                {
                    var page = await native.ReadNativeAsync(context,
                        (client, token) =>
                            client.CoreV1.ListPersistentVolumeAsync(limit: 100,
                                continueParameter: next,
                                cancellationToken: token),
                        ct);
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.CoreV1.WatchListPersistentVolumeAsync(resourceVersion: version,
                        allowWatchBookmarks: true,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => new VolumeItem(Uid(item.Metadata),
                    item.Metadata.Name,
                    StorageObservationValueMappings.From(KubernetesStorageDiscovery.MapPersistentVolume(item))));
            Add<V1PersistentVolumeClaim>("claims",
                async (next, ct) =>
                {
                    var page = await native.ReadNativeAsync(context,
                        (client, token) =>
                            client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(limit: 100,
                                continueParameter: next,
                                cancellationToken: token),
                        ct);
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.CoreV1.WatchListPersistentVolumeClaimForAllNamespacesAsync(
                        resourceVersion: version,
                        allowWatchBookmarks: true,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => new ClaimItem(Uid(item.Metadata),
                    item.Metadata.NamespaceProperty + "/" + item.Metadata.Name,
                    StorageObservationValueMappings.From(KubernetesStorageDiscovery.MapPersistentVolumeClaim(item))));
        }

        private static string Uid(V1ObjectMeta metadata) => metadata.Uid is { Length: > 0 and <= 256 } uid
            ? uid
            : throw new SourceUnavailableException();

        private async IAsyncEnumerable<(WatchEventType Type, T Item)> WatchAsync<T>(CancellationToken ct,
            Func<IKubernetes, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            [EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorToken);
            var client = await native.CurrentClientAsync(context, linked.Token);
            await foreach (var item in watch(client).WithCancellation(linked.Token))
                yield return item;
        }

        private void Add<T>(string stream,
            Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
            Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            Func<T, V1ObjectMeta> metadata,
            Func<T, NativeItem> project) => pumps.Add(PumpAsync(stream, list, watch, metadata, project));

        private async Task PumpAsync<T>(string stream,
            Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
            Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            Func<T, V1ObjectMeta> metadata,
            Func<T, NativeItem> project)
        {
            var retry = TimeSpan.FromMilliseconds(500);
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await foreach (var change in KubernetesListWatch.WatchAsync(list,
                                       watch,
                                       item => metadata(item).ResourceVersion,
                                       clock,
                                       options.MaximumItemsPerKind,
                                       lifetime.Token))
                    {
                        await messages.Writer.WriteAsync(new(stream,
                                change.Kind,
                                change.Items?.Select(project).ToArray(),
                                change.Type,
                                change.Kind == KubernetesListWatchChangeKind.Changed ? project(change.Item!) : null),
                            lifetime.Token);
                        if (change.Kind == KubernetesListWatchChangeKind.Ready)
                            retry = TimeSpan.FromMilliseconds(500);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    if (lifetime.IsCancellationRequested)
                        return;
                    await messages.Writer.WriteAsync(new(stream, KubernetesListWatchChangeKind.Unavailable),
                        lifetime.Token);
                }

                await Task.Delay(retry, clock, lifetime.Token);
                retry = TimeSpan.FromMilliseconds(Math.Min(30_000, retry.TotalMilliseconds * 2));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(pumps);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }

            lifetime.Dispose();
        }
    }
}
