using Pole.Observation;
using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed class RedisResourceDeploymentChangeSource(
    LoadRedisById loadResource,
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
        var resource = deployment.ResourceType == RedisResourceDeployment.ResourceType
            ? await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken)
            : null;
        if (resource is null)
        {
            yield return new(ResourceDeploymentObservationChangeKind.Unavailable);
            yield break;
        }

        var identity = new WorkloadIdentityLabels(deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            RedisResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var namespaceName = deployment.Placement.KubernetesNamespace;
        var selector = string.Join(',', identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));
        var name = resource.Name;
        var serviceName = RedisInKubernetes.BuildServiceName(name);

        bool Owns(V1ObjectMeta? meta, string? exactName = null) =>
            meta is not null && meta.NamespaceProperty == namespaceName &&
            (exactName is null || meta.Name == exactName) && KubernetesWorkloadObservation.Owns(meta, identity);

        KubernetesOwnedWatchItem Project(V1ObjectMeta? meta, bool owned) =>
            KubernetesOwnedWatchItem.FromMetadata(meta, owned);

        var controller = KubernetesOwnedWatchInterest.Create<V1StatefulSet>("controller",
            true,
            async (continuation, ct) =>
            {
                var page = await ReadAsync(context,
                    (client, token) => client.AppsV1.ListNamespacedStatefulSetAsync(namespaceName,
                        fieldSelector: $"metadata.name={name}",
                        labelSelector: selector,
                        limit: 100,
                        continueParameter: continuation,
                        cancellationToken: token),
                    ct);
                return new(page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
            },
            (version, error, ct) => WatchAsync(context,
                ct,
                client => client.AppsV1.WatchListNamespacedStatefulSetAsync(namespaceName,
                    allowWatchBookmarks: true,
                    fieldSelector: $"metadata.name={name}",
                    labelSelector: selector,
                    resourceVersion: version,
                    timeoutSeconds: 300,
                    onError: error,
                    cancellationToken: ct)),
            item => item.Metadata?.ResourceVersion,
            item => Project(item.Metadata, Owns(item.Metadata, name)),
            clock);
        await using var session = new KubernetesOwnedWatchSession(controller, Dependents, clock, cancellationToken);
        await foreach (var change in session.RunAsync(cancellationToken))
            yield return new(change.Kind switch
            {
                KubernetesOwnedWatchChangeKind.Ready => ResourceDeploymentObservationChangeKind.SourceReady,
                KubernetesOwnedWatchChangeKind.Invalidated => ResourceDeploymentObservationChangeKind.Invalidated,
                KubernetesOwnedWatchChangeKind.BindingChanged => ResourceDeploymentObservationChangeKind
                    .BindingChanged,
                KubernetesOwnedWatchChangeKind.Unavailable => ResourceDeploymentObservationChangeKind.Unavailable,
                _ => throw new InvalidOperationException("UnknownNativeChange")
            },
                change.BindingId);

        IReadOnlyList<KubernetesOwnedWatchInterest> Dependents(string binding,
            IReadOnlyDictionary<string, IReadOnlyList<KubernetesOwnedWatchItem>> graph)
        {
            var result = new List<KubernetesOwnedWatchInterest>
            {
                KubernetesOwnedWatchInterest.Create<V1Pod>("pods",
                    false,
                    async (continuation, ct) =>
                    {
                        var page = await ReadAsync(context,
                            (client, token) => client.CoreV1.ListNamespacedPodAsync(namespaceName,
                                labelSelector: selector,
                                limit: 100,
                                continueParameter: continuation,
                                cancellationToken: token),
                            ct);
                        return new(page.Items.ToArray(),
                            page.Metadata?.ResourceVersion,
                            page.Metadata?.ContinueProperty);
                    },
                    (version, error, ct) => WatchAsync(context,
                        ct,
                        client => client.CoreV1.WatchListNamespacedPodAsync(namespaceName,
                            allowWatchBookmarks: true,
                            labelSelector: selector,
                            resourceVersion: version,
                            timeoutSeconds: 300,
                            onError: error,
                            cancellationToken: ct)),
                    item => item.Metadata?.ResourceVersion,
                    item => Project(item.Metadata,
                        Owns(item.Metadata) &&
                        KubernetesOwnedWatchItem.ControlledBy(item.Metadata, binding, "StatefulSet", "apps/v1")),
                    clock),
                KubernetesOwnedWatchInterest.Create<V1Service>("service",
                    true,
                    async (continuation, ct) =>
                    {
                        var page = await ReadAsync(context,
                            (client, token) => client.CoreV1.ListNamespacedServiceAsync(namespaceName,
                                fieldSelector: $"metadata.name={serviceName}",
                                labelSelector: selector,
                                limit: 100,
                                continueParameter: continuation,
                                cancellationToken: token),
                            ct);
                        return new(page.Items.ToArray(),
                            page.Metadata?.ResourceVersion,
                            page.Metadata?.ContinueProperty);
                    },
                    (version, error, ct) => WatchAsync(context,
                        ct,
                        client => client.CoreV1.WatchListNamespacedServiceAsync(namespaceName,
                            allowWatchBookmarks: true,
                            fieldSelector: $"metadata.name={serviceName}",
                            labelSelector: selector,
                            resourceVersion: version,
                            timeoutSeconds: 300,
                            onError: error,
                            cancellationToken: ct)),
                    item => item.Metadata?.ResourceVersion,
                    item => Project(item.Metadata, Owns(item.Metadata, serviceName)),
                    clock)
            };
            var pods = graph.GetValueOrDefault("pods") ?? [];
            foreach (var uid in new[] { binding }.Concat(pods.Select(pod => pod.Uid).Order(StringComparer.Ordinal))
                         .Distinct(StringComparer.Ordinal).Take(8))
                result.Add(KubernetesOwnedWatchInterest.Create<Corev1Event>("warning:" + uid,
                    false,
                    async (continuation, ct) =>
                    {
                        var page = await ReadAsync(context,
                            (client, token) => client.CoreV1.ListNamespacedEventAsync(namespaceName,
                                fieldSelector: $"involvedObject.uid={uid},type=Warning",
                                limit: 100,
                                continueParameter: continuation,
                                cancellationToken: token),
                            ct);
                        return new(page.Items.ToArray(),
                            page.Metadata?.ResourceVersion,
                            page.Metadata?.ContinueProperty);
                    },
                    (version, error, ct) => WatchAsync(context,
                        ct,
                        client => client.CoreV1.WatchListNamespacedEventAsync(namespaceName,
                            allowWatchBookmarks: true,
                            fieldSelector: $"involvedObject.uid={uid},type=Warning",
                            resourceVersion: version,
                            timeoutSeconds: 300,
                            onError: error,
                            cancellationToken: ct)),
                    item => item.Metadata?.ResourceVersion,
                    item => Project(item.Metadata,
                        item.Metadata?.NamespaceProperty == namespaceName && !string.IsNullOrEmpty(item.Metadata.Uid) &&
                        item.Type == "Warning" && item.InvolvedObject?.Uid == uid),
                    clock));
            foreach (var service in graph.GetValueOrDefault("service") ?? [])
                result.Add(KubernetesOwnedWatchInterest.Create<V1EndpointSlice>("slice:" + service.Uid,
                    false,
                    async (continuation, ct) =>
                    {
                        var page = await ReadAsync(context,
                            (client, token) => client.DiscoveryV1.ListNamespacedEndpointSliceAsync(namespaceName,
                                labelSelector: $"kubernetes.io/service-name={serviceName}",
                                limit: 100,
                                continueParameter: continuation,
                                cancellationToken: token),
                            ct);
                        return new(page.Items.ToArray(),
                            page.Metadata?.ResourceVersion,
                            page.Metadata?.ContinueProperty);
                    },
                    (version, error, ct) => WatchAsync(context,
                        ct,
                        client => client.DiscoveryV1.WatchListNamespacedEndpointSliceAsync(namespaceName,
                            allowWatchBookmarks: true,
                            labelSelector: $"kubernetes.io/service-name={serviceName}",
                            resourceVersion: version,
                            timeoutSeconds: 300,
                            onError: error,
                            cancellationToken: ct)),
                    item => item.Metadata?.ResourceVersion,
                    item => Project(item.Metadata,
                        item.Metadata?.NamespaceProperty == namespaceName && !string.IsNullOrEmpty(item.Metadata.Uid) &&
                        item.Metadata.Labels?.TryGetValue("kubernetes.io/service-name", out var serviceLabel) == true &&
                        serviceLabel == serviceName &&
                        KubernetesOwnedWatchItem.ControlledBy(item.Metadata, service.Uid, "Service", "v1")),
                    clock));
            return result;
        }
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

    private Task<T> ReadAsync<T>(ResourceDeploymentObservationContext context,
        Func<IKubernetes, CancellationToken, Task<T>> read,
        CancellationToken ct) =>
        budget.ReadAsync(async token => await read(await CurrentClientAsync(context, token), token), ct);

    private async IAsyncEnumerable<(WatchEventType Type, T Item)> WatchAsync<T>(
        ResourceDeploymentObservationContext context,
        [EnumeratorCancellation] CancellationToken ct,
        Func<IKubernetes, IAsyncEnumerable<(WatchEventType Type, T Item)>> watch)
    {
        var client = await CurrentClientAsync(context, ct);
        await foreach (var item in watch(client).WithCancellation(ct))
            yield return item;
    }
}
