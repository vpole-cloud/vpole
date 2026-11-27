// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using k8s.Models;

namespace Pole.Features.Core.Kubernetes;

/// <summary>Bounded metadata only; native specifications and messages never enter the session.</summary>
public sealed record KubernetesOwnedWatchItem(
    string Uid,
    string Name,
    bool Owned
)
{
    public static KubernetesOwnedWatchItem FromMetadata(V1ObjectMeta? metadata, bool owned) => new(
        Bounded(metadata?.Uid, 256),
        Bounded(metadata?.Name, 253),
        owned);

    public static bool ControlledBy(
        V1ObjectMeta? metadata,
        string uid,
        string kind,
        string apiVersion)
    {
        var owners = metadata?.OwnerReferences?.Where(owner => owner.Controller == true).Take(2).ToArray() ?? [];
        foreach (var owner in owners)
            Bounded(owner.Uid, 256);
        return owners.Length == 1 && owners[0].Uid == uid && owners[0].Kind == kind &&
            owners[0].ApiVersion == apiVersion;
    }

    private static string Bounded(string? value, int limit) => value?.Length > limit
        ? throw new InvalidOperationException("NativeMetadataLimit")
        : value ?? "";
}

public sealed record KubernetesOwnedWatchInterest(
    string Key,
    bool Singleton,
    Func<CancellationToken, IAsyncEnumerable<KubernetesListWatchChange<KubernetesOwnedWatchItem>>> Watch
)
{
    public static KubernetesOwnedWatchInterest Create<T>(
        string key,
        bool singleton,
        Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
        Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
        Func<T, string?> version,
        Func<T, KubernetesOwnedWatchItem> project,
        TimeProvider clock) => new(key, singleton, token => ProjectAsync(list, watch, version, project, clock, token));

    private static async IAsyncEnumerable<KubernetesListWatchChange<KubernetesOwnedWatchItem>> ProjectAsync<T>(
        Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
        Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
        Func<T, string?> version,
        Func<T, KubernetesOwnedWatchItem> project,
        TimeProvider clock,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var change in KubernetesListWatch.WatchAsync(
                           list,
                           watch,
                           version,
                           clock,
                           cancellationToken: token))
            yield return new KubernetesListWatchChange<KubernetesOwnedWatchItem>(
                change.Kind,
                change.Items?.Select(project).ToArray(),
                change.Type,
                change.Kind == KubernetesListWatchChangeKind.Changed ? project(change.Item!) : null);
    }
}

public enum KubernetesOwnedWatchChangeKind
{
    Ready,
    Invalidated,
    BindingChanged,
    Unavailable,
}

public sealed record KubernetesOwnedWatchChange(
    KubernetesOwnedWatchChangeKind Kind,
    string? BindingId
);

/// <summary>Technical stream lifecycle; providers supply identity, graph topology and native factories.</summary>
public sealed class KubernetesOwnedWatchSession(
    KubernetesOwnedWatchInterest controller,
    Func<string, IReadOnlyDictionary<string, IReadOnlyList<KubernetesOwnedWatchItem>>,
        IReadOnlyList<KubernetesOwnedWatchInterest>> dependents,
    TimeProvider clock,
    CancellationToken cancellationToken
) : IAsyncDisposable
{
    private readonly List<StreamState> _allStreams = [];

    private readonly CancellationTokenSource _lifetime
        = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    private readonly Channel<Message> _messages = Channel.CreateBounded<Message>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private string? _binding;
    private bool _covered;
    private long _sequence;

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await Task.WhenAll(_allStreams.Select(stream => stream.Pump));
        foreach (var stream in _allStreams)
            stream.Cancellation.Dispose();
        _lifetime.Dispose();
        _messages.Writer.TryComplete();
    }

    /// <summary>Lazy async setup for providers without an async-iterator language construct.</summary>
    public static async IAsyncEnumerable<KubernetesOwnedWatchChange> StartAsync(
        Func<CancellationToken, Task<KubernetesOwnedWatchSession?>> create,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = await create(cancellationToken);
        if (session is null)
        {
            yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Unavailable, null);
            yield break;
        }

        await foreach (var change in session.RunAsync(cancellationToken))
            yield return change;
    }

    public async IAsyncEnumerable<KubernetesOwnedWatchChange> RunAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var cancellation = token.Register(_lifetime.Cancel);
        Add(controller);
        while (await _messages.Reader.WaitToReadAsync(_lifetime.Token))
        while (_messages.Reader.TryRead(out var message))
        {
            ReapRetired();
            if (!_streams.TryGetValue(message.Key, out var state) || state.Sequence != message.Sequence)
                continue;
            var previousCoverage = _covered;
            var change = message.Change;
            if (change.Kind == KubernetesListWatchChangeKind.Unavailable)
            {
                state.Ready = false;
                _covered = false;
                yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Unavailable, _binding);
                continue;
            }

            var invalidated = false;
            if (change.Kind == KubernetesListWatchChangeKind.Ready)
                state.Ready = true;
            if (change.Kind == KubernetesListWatchChangeKind.Listed)
            {
                state.Listed = true;
                invalidated = state.Items.Count > 0;
                state.Items.Clear();
                foreach (var item in change.Items!)
                    if (item.Owned)
                        state.Items[item.Uid] = item;
                invalidated |= state.Items.Count > 0;
            }
            else if (change.Kind == KubernetesListWatchChangeKind.Changed && change.Item is { } item)
            {
                invalidated = state.Items.Remove(item.Uid);
                if (change.Type != WatchEventType.Deleted && item.Owned)
                {
                    if (state.Interest.Singleton)
                        state.Items.Clear();
                    if (state.Items.Count >= 100)
                    {
                        _covered = false;
                        state.Ready = false;
                        _lifetime.Cancel();
                        yield return new KubernetesOwnedWatchChange(
                            KubernetesOwnedWatchChangeKind.Unavailable,
                            _binding);
                        throw new InvalidOperationException("NativeObjectLimit");
                    }

                    state.Items[item.Uid] = item;
                    invalidated = true;
                }
            }

            if (message.Key == controller.Key &&
                change.Kind is KubernetesListWatchChangeKind.Listed or KubernetesListWatchChangeKind.Changed)
            {
                var next = state.Items.Count == 1 ? state.Items.Keys.Single() : null;
                if (next != _binding)
                {
                    _binding = next;
                    _covered = false;
                    foreach (var key in _streams.Keys.Where(key => key != controller.Key).ToArray())
                        Retire(key);
                    // Negative/replacement fence is observed before any dependent LIST is started.
                    yield return new KubernetesOwnedWatchChange(
                        KubernetesOwnedWatchChangeKind.BindingChanged,
                        _binding);
                }
            }

            if (_binding is not null)
                RefreshDependents();
            _covered = _binding is not null && _streams.Count > 1 &&
                _streams.Values.All(stream => stream.Listed && stream.Ready);
            if (previousCoverage && !_covered)
                yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Unavailable, _binding);
            if (invalidated && _binding is not null)
                yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Invalidated, _binding);
            if (_covered && !previousCoverage)
                yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Ready, _binding);
            if (_binding is null && message.Key == controller.Key && change.Kind == KubernetesListWatchChangeKind.Ready)
                yield return new KubernetesOwnedWatchChange(KubernetesOwnedWatchChangeKind.Unavailable, null);
        }
    }

    private void RefreshDependents()
    {
        var graph = _streams.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<KubernetesOwnedWatchItem>)pair.Value.Items.Values.ToArray(),
            StringComparer.Ordinal);
        var desired = dependents(_binding!, graph).ToDictionary(interest => interest.Key, StringComparer.Ordinal);
        if (desired.ContainsKey(controller.Key))
            throw new InvalidOperationException("NativeInterestConflict");
        foreach (var key in _streams.Keys.Where(key => key != controller.Key && !desired.ContainsKey(key)).ToArray())
            Retire(key);
        foreach (var interest in desired.Values)
            if (!_streams.ContainsKey(interest.Key))
                Add(interest);
    }

    private void Add(KubernetesOwnedWatchInterest interest)
    {
        var state = new StreamState(
            interest,
            ++_sequence,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _streams.Add(interest.Key, state);
        _allStreams.Add(state);
        state.Pump = PumpAsync(state);
    }

    private void Retire(string key)
    {
        if (_streams.Remove(key, out var state))
        {
            state.Retired = true;
            state.Cancellation.Cancel();
        }
    }

    private void ReapRetired()
    {
        for (var index = _allStreams.Count - 1; index >= 0; index--)
            if (_allStreams[index].Retired && _allStreams[index].Pump.IsCompleted)
            {
                _allStreams[index].Cancellation.Dispose();
                _allStreams.RemoveAt(index);
            }
    }

    private async Task PumpAsync(StreamState state)
    {
        var retry = TimeSpan.FromMilliseconds(500);
        while (!state.Cancellation.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in state.Interest.Watch(state.Cancellation.Token))
                {
                    await _messages.Writer.WriteAsync(
                        new Message(state.Interest.Key, state.Sequence, change),
                        state.Cancellation.Token);
                    if (change.Kind == KubernetesListWatchChangeKind.Ready)
                        retry = TimeSpan.FromMilliseconds(500);
                }
            }
            catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Fail closed and restart fresh after unexpected native projection/lookup failures.
                if (state.Cancellation.IsCancellationRequested)
                    return;
                try
                {
                    await _messages.Writer.WriteAsync(
                        new Message(
                            state.Interest.Key,
                            state.Sequence,
                            new KubernetesListWatchChange<KubernetesOwnedWatchItem>(
                                KubernetesListWatchChangeKind.Unavailable)),
                        state.Cancellation.Token);
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(retry, clock, state.Cancellation.Token);
            }
            catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
            {
                return;
            }

            retry = TimeSpan.FromMilliseconds(Math.Min(retry.TotalMilliseconds * 2, 30_000));
        }
    }

    private sealed class StreamState(
        KubernetesOwnedWatchInterest interest,
        long sequence,
        CancellationTokenSource cancellation
    )
    {
        public KubernetesOwnedWatchInterest Interest { get; } = interest;

        public long Sequence { get; } = sequence;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Pump { get; set; } = Task.CompletedTask;

        public bool Listed { get; set; }

        public bool Ready { get; set; }

        public bool Retired { get; set; }

        public Dictionary<string, KubernetesOwnedWatchItem> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Message(
        string Key,
        long Sequence,
        KubernetesListWatchChange<KubernetesOwnedWatchItem> Change
    );
}
