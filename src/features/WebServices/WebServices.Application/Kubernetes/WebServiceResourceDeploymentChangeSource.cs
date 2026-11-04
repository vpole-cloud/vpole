using Pole.Observation;
using Pole.Features.WebServices.Domain;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Reads;

namespace Pole.Features.WebServices.Application.Kubernetes;

/// <summary>Owns native WebService identity; only opaque binding/change hints leave this provider.</summary>
public sealed class WebServiceResourceDeploymentChangeSource(
    LoadWebServiceById loadResource,
    IKubernetesClientProvider clients,
    LookupDeploymentTarget lookupTarget,
    IObservationReadBudget budget,
    TimeProvider clock) : IResourceDeploymentObservationChangeSource
{
    public async IAsyncEnumerable<ResourceDeploymentObservationChange> WatchAsync(
        ResourceDeploymentObservationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        var resource = deployment.ResourceType == WebServiceResourceDeployment.ResourceType
            ? await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken)
            : null;
        if (resource is null)
        {
            yield return new(ResourceDeploymentObservationChangeKind.Unavailable);
            yield break;
        }

        await using var source = new SourceSession(this, context, resource, clock, cancellationToken);
        await foreach (var change in source.RunAsync(cancellationToken))
            yield return change;
    }

    private async Task<IKubernetes> CurrentClientAsync(ResourceDeploymentObservationContext context,
        CancellationToken ct)
    {
        var targetId = context.Deployment.Placement.DeploymentTargetId;
        if (await lookupTarget(targetId, ct) is not DeploymentTargetLookupDto
            {
                Enabled: true, DeletionRequested: false
            } before ||
            before.ConnectionVersion != context.TargetConnectionVersion)
            throw new HttpRequestException("ObservationTargetChanged");
        IKubernetes client;
        try
        {
            client = await clients.GetAsync(targetId, ct);
        }
        catch (DeploymentTargetUnavailableException)
        {
            throw new HttpRequestException("ObservationTargetUnavailable");
        }

        if (await lookupTarget(targetId, ct) is not DeploymentTargetLookupDto
            {
                Enabled: true, DeletionRequested: false
            } after ||
            after.ConnectionVersion != context.TargetConnectionVersion)
            throw new HttpRequestException("ObservationTargetChanged");
        return client;
    }

    private Task<T> ReadNativeAsync<T>(ResourceDeploymentObservationContext context,
        Func<IKubernetes, CancellationToken, Task<T>> read,
        CancellationToken ct) =>
        budget.ReadAsync(async token => await read(await CurrentClientAsync(context, token), token), ct);

    private sealed class SourceUnavailableException(string code) : InvalidOperationException(code);

    private sealed record NativeObject(
        string Uid,
        string Name,
        string? ParentUid,
        bool Owned);

    private sealed record NativeMessage(
        string Stream,
        long Sequence,
        KubernetesListWatchChangeKind Kind,
        IReadOnlyList<NativeObject>? Items = null,
        WatchEventType? Type = null,
        NativeObject? Item = null);

    private sealed class StreamState(long sequence, CancellationTokenSource cancellation)
    {
        public long Sequence { get; } = sequence;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Pump { get; set; } = Task.CompletedTask;

        public bool Listed { get; set; }

        public bool Ready { get; set; }

        public bool Retired { get; set; }

        public Dictionary<string, NativeObject> Items { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Per-interest UID graphs and transports. Native specs never enter its bounded message queue.</summary>
    private sealed class SourceSession : IAsyncDisposable
    {
        private const string DeploymentStream = "deployment";
        private const string ReplicaStream = "replicas";
        private const string PodStream = "pods";
        private const string ServiceStream = "service";
        private const string IngressStream = "ingress";
        private const string AutoscalerStream = "autoscaler";
        private const string WarningPrefix = "warning:";
        private const int MaximumObjectsPerStream = 100;
        private const int MaximumUidLength = 256;
        private const int MaximumNameLength = 253;
        private readonly WebServiceResourceDeploymentChangeSource _owner;
        private readonly ResourceDeploymentObservationContext _context;
        private readonly string _name;
        private readonly bool _observeAutoscaler;
        private readonly string _namespace;
        private readonly WorkloadIdentityLabels _identity;
        private readonly string _selector;
        private readonly TimeProvider _clock;
        private readonly CancellationTokenSource _lifetime;

        private readonly Channel<NativeMessage> _messages = Channel.CreateBounded<NativeMessage>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
        private readonly List<StreamState> _allStreams = [];
        private long _sequence;
        private string? _binding;
        private bool _covered;

        public SourceSession(WebServiceResourceDeploymentChangeSource owner,
            ResourceDeploymentObservationContext context,
            WebService resource,
            TimeProvider clock,
            CancellationToken ct)
        {
            _owner = owner;
            _context = context;
            _name = resource.Name;
            _observeAutoscaler = resource.Spec.Autoscaling is not null;
            _namespace = context.Deployment.Placement.KubernetesNamespace;
            _identity = new(context.Deployment.TenantId,
                context.Deployment.ResourceGroupId,
                context.Deployment.IsolationScopeId,
                context.Deployment.RuntimeCellId,
                context.Deployment.Placement.DeploymentTargetId,
                WebServiceResourceDeployment.KubernetesResourceTypeLabel,
                context.Deployment.ResourceId,
                context.Deployment.Id);
            _selector = string.Join(',', _identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));
            _clock = clock;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public async IAsyncEnumerable<ResourceDeploymentObservationChange> RunAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            StartDeployment();
            while (await _messages.Reader.WaitToReadAsync(ct))
            {
                while (_messages.Reader.TryRead(out var message))
                {
                    ReapRetired();
                    if (!_streams.TryGetValue(message.Stream, out var state) || state.Sequence != message.Sequence)
                        continue;
                    var wasCovered = _covered;
                    var invalidate = false;
                    if (message.Kind == KubernetesListWatchChangeKind.Unavailable)
                    {
                        state.Ready = false;
                        _covered = false;
                        yield return new(ResourceDeploymentObservationChangeKind.Unavailable, _binding);
                        continue;
                    }

                    if (message.Kind == KubernetesListWatchChangeKind.Ready)
                        state.Ready = true;
                    if (message.Kind == KubernetesListWatchChangeKind.Listed)
                    {
                        state.Listed = true;
                        invalidate = state.Items.Values.Any(item => IsBoundObject(message.Stream, item));
                        state.Items.Clear();
                        foreach (var item in message.Items!)
                            if (item.Owned)
                                state.Items[item.Uid] = item;
                        invalidate |= state.Items.Values.Any(item => IsBoundObject(message.Stream, item));
                    }
                    else if (message.Kind == KubernetesListWatchChangeKind.Changed && message.Item is { } item)
                    {
                        // A selector-loss tombstone can carry foreign labels: retire a previously accepted UID.
                        var previouslyOwned = state.Items.TryGetValue(item.Uid, out var previous) &&
                                              IsBoundObject(message.Stream, previous);
                        state.Items.Remove(item.Uid);
                        if (message.Type != WatchEventType.Deleted && item.Owned)
                        {
                            if (message.Stream is DeploymentStream or ServiceStream or IngressStream or
                                AutoscalerStream)
                                state.Items.Clear();
                            if (state.Items.Count >= MaximumObjectsPerStream)
                            {
                                // Preserve the complete previous graph; never invent removals to fit a live update.
                                _covered = false;
                                state.Ready = false;
                                _lifetime.Cancel();
                                yield return new(ResourceDeploymentObservationChangeKind.Unavailable, _binding);
                                throw new SourceUnavailableException("NativeObjectLimit");
                            }

                            state.Items[item.Uid] = item;
                        }

                        invalidate = previouslyOwned || IsBoundObject(message.Stream, item);
                    }

                    if (message.Stream == DeploymentStream && message.Kind is KubernetesListWatchChangeKind.Listed
                            or KubernetesListWatchChangeKind.Changed)
                    {
                        var next = state.Items.Count == 1 ? state.Items.Keys.Single() : null;
                        if (next != _binding)
                        {
                            _binding = next;
                            _covered = false;
                            RetireDependents();
                            // Deliver this fence before starting/awaiting any replacement native reads.
                            yield return new(ResourceDeploymentObservationChangeKind.BindingChanged, _binding);
                            if (_binding is not null)
                                StartDependents();
                        }
                    }

                    if (_binding is not null && message.Stream is DeploymentStream or ReplicaStream or PodStream)
                        RefreshWarnings();
                    _covered = _binding is not null && _streams.Count >= 6 &&
                               _streams.Values.All(stream => stream.Listed && stream.Ready);
                    if (wasCovered && !_covered)
                        yield return new(ResourceDeploymentObservationChangeKind.Unavailable, _binding);
                    if (invalidate && _binding is not null)
                        yield return new(ResourceDeploymentObservationChangeKind.Invalidated, _binding);
                    if (_covered && !wasCovered)
                        yield return new(ResourceDeploymentObservationChangeKind.SourceReady, _binding);
                    // Empty/foreign controller LIST keeps its WATCH open so owned ADDED can recover coverage.
                    if (_binding is null && message.Stream == DeploymentStream &&
                        message.Kind == KubernetesListWatchChangeKind.Ready)
                        yield return new(ResourceDeploymentObservationChangeKind.Unavailable);
                }
            }
        }

        private void RetireDependents()
        {
            foreach (var key in _streams.Keys.Where(key => key != DeploymentStream).ToArray())
                Retire(key);
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

        private NativeObject Project(V1ObjectMeta? metadata, string? exactName = null, bool requireParent = false)
        {
            var parents = metadata?.OwnerReferences?.Where(owner => owner.Controller == true).Take(2).ToArray() ?? [];
            var parent = parents.Length == 1 ? parents[0].Uid : null;
            var owned = metadata is not null && metadata.NamespaceProperty == _namespace &&
                        (exactName is null || metadata.Name == exactName) &&
                        KubernetesWorkloadObservation.Owns(metadata, _identity) &&
                        (!requireParent || !string.IsNullOrEmpty(parent));
            return new(Bounded(metadata?.Uid, MaximumUidLength),
                Bounded(metadata?.Name, MaximumNameLength),
                parent is null ? null : Bounded(parent, MaximumUidLength),
                owned);
        }

        private static string Bounded(string? value, int maximumLength) => value?.Length > maximumLength
            ? throw new SourceUnavailableException("NativeMetadataLimit")
            : value ?? "";

        private NativeObject ProjectWarning(Corev1Event item, string uid) => new(
            Bounded(item.Metadata?.Uid, MaximumUidLength),
            "",
            null,
            !string.IsNullOrEmpty(item.Metadata?.Uid) && item.Metadata.NamespaceProperty == _namespace &&
            item.InvolvedObject?.Uid == uid && item.Type == "Warning");

        private bool IsBoundObject(string stream, NativeObject item) => item.Owned && stream switch
        {
            ReplicaStream => item.ParentUid == _binding,
            PodStream => item.ParentUid is not null && _streams.TryGetValue(ReplicaStream, out var rs) &&
                         rs.Items.TryGetValue(item.ParentUid, out var replica) && replica.ParentUid == _binding,
            _ => true
        };

        private void RefreshWarnings()
        {
            var replicas = _streams.TryGetValue(ReplicaStream, out var rs)
                ? rs.Items.Values.Where(item => item.ParentUid == _binding).ToArray()
                : [];
            var replicaUids = replicas.Select(item => item.Uid).ToHashSet(StringComparer.Ordinal);
            var pods = _streams.TryGetValue(PodStream, out var ps)
                ? ps.Items.Values.Where(item => item.ParentUid is not null && replicaUids.Contains(item.ParentUid))
                    .ToArray()
                : [];
            // Same deterministic selection as both bounded health and runtime samplers.
            var required = new[] { _binding! }.Concat(replicas.Select(item => item.Uid).Order(StringComparer.Ordinal))
                .Concat(pods.Select(item => item.Uid).Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)
                .Take(8)
                .Select(uid => WarningPrefix + uid).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _streams.Keys.Where(key =>
                         key.StartsWith(WarningPrefix, StringComparison.Ordinal) && !required.Contains(key)).ToArray())
                Retire(key);
            foreach (var key in required.Where(key => !_streams.ContainsKey(key)))
                StartWarning(key[WarningPrefix.Length..]);
        }

        private void StartDeployment() => Add<V1Deployment>(DeploymentStream,
            async (continuation, ct) =>
            {
                var page = await ReadAsync(ct,
                    (client, token) => client.AppsV1.ListNamespacedDeploymentAsync(_namespace,
                        fieldSelector: $"metadata.name={_name}",
                        labelSelector: _selector,
                        limit: 100,
                        continueParameter: continuation,
                        cancellationToken: token));
                return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
            },
            (version, error, ct) => WatchAsync(ct,
                client => client.AppsV1.WatchListNamespacedDeploymentAsync(_namespace,
                    allowWatchBookmarks: true,
                    fieldSelector: $"metadata.name={_name}",
                    labelSelector: _selector,
                    resourceVersion: version,
                    timeoutSeconds: 300,
                    onError: error,
                    cancellationToken: ct)),
            item => item.Metadata,
            item => Project(item.Metadata, _name));

        private void StartDependents()
        {
            if (_observeAutoscaler)
                StartAutoscaler();


            Add<V1ReplicaSet>(ReplicaStream,
                async (continuation, ct) =>
                {
                    var page = await ReadAsync(ct,
                        (client, token) => client.AppsV1.ListNamespacedReplicaSetAsync(_namespace,
                            labelSelector: _selector,
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token));
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.AppsV1.WatchListNamespacedReplicaSetAsync(_namespace,
                        allowWatchBookmarks: true,
                        labelSelector: _selector,
                        resourceVersion: version,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => Project(item.Metadata, requireParent: true));
            Add<V1Pod>(PodStream,
                async (continuation, ct) =>
                {
                    var page = await ReadAsync(ct,
                        (client, token) => client.CoreV1.ListNamespacedPodAsync(_namespace,
                            labelSelector: _selector,
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token));
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.CoreV1.WatchListNamespacedPodAsync(_namespace,
                        allowWatchBookmarks: true,
                        labelSelector: _selector,
                        resourceVersion: version,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => Project(item.Metadata, requireParent: true));
            var serviceName = WebServiceInKubernetes.BuildServiceName(_name);
            Add<V1Service>(ServiceStream,
                async (continuation, ct) =>
                {
                    var page = await ReadAsync(ct,
                        (client, token) => client.CoreV1.ListNamespacedServiceAsync(_namespace,
                            fieldSelector: $"metadata.name={serviceName}",
                            labelSelector: _selector,
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token));
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.CoreV1.WatchListNamespacedServiceAsync(_namespace,
                        allowWatchBookmarks: true,
                        fieldSelector: $"metadata.name={serviceName}",
                        labelSelector: _selector,
                        resourceVersion: version,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => Project(item.Metadata, serviceName));
            var ingressName = WebServiceInKubernetes.BuildIngressName(_name);
            Add<V1Ingress>(IngressStream,
                async (continuation, ct) =>
                {
                    var page = await ReadAsync(ct,
                        (client, token) => client.NetworkingV1.ListNamespacedIngressAsync(_namespace,
                            fieldSelector: $"metadata.name={ingressName}",
                            labelSelector: _selector,
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token));
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.NetworkingV1.WatchListNamespacedIngressAsync(_namespace,
                        allowWatchBookmarks: true,
                        fieldSelector: $"metadata.name={ingressName}",
                        labelSelector: _selector,
                        resourceVersion: version,
                        timeoutSeconds: 300,
                        onError: error,
                        cancellationToken: ct)),
                item => item.Metadata,
                item => Project(item.Metadata, ingressName));
        }

        private void StartAutoscaler()
        {
            var name = WebServiceInKubernetes.BuildAutoscalerName(_name);
            Add<V2HorizontalPodAutoscaler>(AutoscalerStream,
                async (continuation, ct) =>
                {
                    var page = await ReadAsync(ct,
                        (client, token) => client.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(_namespace,
                            fieldSelector: $"metadata.name={name}", labelSelector: _selector,
                            limit: 100, continueParameter: continuation, cancellationToken: token));
                    return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                },
                (version, error, ct) => WatchAsync(ct,
                    client => client.AutoscalingV2.WatchListNamespacedHorizontalPodAutoscalerAsync(_namespace,
                        allowWatchBookmarks: true, fieldSelector: $"metadata.name={name}", labelSelector: _selector,
                        resourceVersion: version, timeoutSeconds: 300, onError: error, cancellationToken: ct)),
                item => item.Metadata, item => Project(item.Metadata, name));
        }

        private void StartWarning(string uid) => Add<Corev1Event>(WarningPrefix + uid,
            async (continuation, ct) =>
            {
                var page = await ReadAsync(ct,
                    (client, token) => client.CoreV1.ListNamespacedEventAsync(_namespace,
                        fieldSelector: $"involvedObject.uid={uid},type=Warning",
                        limit: 4,
                        continueParameter: continuation,
                        cancellationToken: token));
                return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
            },
            (version, error, ct) => WatchAsync(ct,
                client => client.CoreV1.WatchListNamespacedEventAsync(_namespace,
                    allowWatchBookmarks: true,
                    fieldSelector: $"involvedObject.uid={uid},type=Warning",
                    resourceVersion: version,
                    timeoutSeconds: 300,
                    onError: error,
                    cancellationToken: ct)),
            item => item.Metadata,
            item => ProjectWarning(item, uid));

        private Task<T> ReadAsync<T>(CancellationToken ct, Func<IKubernetes, CancellationToken, Task<T>> read) =>
            _owner.ReadNativeAsync(_context, read, ct);

        private async IAsyncEnumerable<(WatchEventType Type, T Item)> WatchAsync<T>(CancellationToken ct,
            Func<IKubernetes, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            [EnumeratorCancellation] CancellationToken enumeratorToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorToken);
            var client = await _owner.CurrentClientAsync(_context, linked.Token);
            await foreach (var item in watch(client).WithCancellation(linked.Token))
                yield return item;
        }

        private void Add<T>(string key,
            Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
            Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            Func<T, V1ObjectMeta?> metadata,
            Func<T, NativeObject> project)
        {
            var state = new StreamState(++_sequence, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _streams.Add(key, state);
            _allStreams.Add(state);
            state.Pump = PumpAsync(key, state, list, watch, metadata, project);
        }

        private async Task PumpAsync<T>(string key,
            StreamState state,
            Func<string?, CancellationToken, Task<KubernetesListPage<T>>> list,
            Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch,
            Func<T, V1ObjectMeta?> metadata,
            Func<T, NativeObject> project)
        {
            var retry = TimeSpan.FromMilliseconds(500);
            while (!state.Cancellation.IsCancellationRequested)
            {
                try
                {
                    await foreach (var change in KubernetesListWatch.WatchAsync(list,
                                       watch,
                                       item => metadata(item)?.ResourceVersion,
                                       _clock,
                                       cancellationToken: state.Cancellation.Token))
                    {
                        var message = new NativeMessage(key,
                            state.Sequence,
                            change.Kind,
                            change.Items?.Select(project).ToArray(),
                            change.Type,
                            change.Kind == KubernetesListWatchChangeKind.Changed ? project(change.Item!) : null);
                        await _messages.Writer.WriteAsync(message, state.Cancellation.Token);
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
                    // Background aggregate/lookup failure disables coverage; native diagnostics never escape.
                    if (state.Cancellation.IsCancellationRequested)
                        return;
                    try
                    {
                        await _messages.Writer.WriteAsync(
                            new(key, state.Sequence, KubernetesListWatchChangeKind.Unavailable),
                            state.Cancellation.Token);
                    }
                    catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                }

                try
                {
                    await Task.Delay(retry, _clock, state.Cancellation.Token);
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    return;
                }

                retry = TimeSpan.FromMilliseconds(Math.Min(retry.TotalMilliseconds * 2, 30_000));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await Task.WhenAll(_allStreams.Select(stream => stream.Pump));
            foreach (var stream in _allStreams)
                stream.Cancellation.Dispose();
            _lifetime.Dispose();
            _messages.Writer.TryComplete();
        }
    }
}
