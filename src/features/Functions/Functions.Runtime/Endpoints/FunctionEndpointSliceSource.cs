using System.Runtime.ExceptionServices;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.Functions.Runtime.Endpoints;

public sealed record FunctionEndpointSlicePage(
    IList<V1EndpointSlice> Items,
    string? ResourceVersion,
    string? ContinuationToken);

public interface IFunctionEndpointSliceSource
{
    Task<FunctionEndpointSlicePage> ListPageAsync(
        string? continuationToken,
        CancellationToken cancellationToken);

    IAsyncEnumerable<(WatchEventType Type, V1EndpointSlice Slice)> WatchAsync(
        string resourceVersion,
        CancellationToken cancellationToken);
}

public sealed class KubernetesFunctionEndpointSliceSource(IKubernetesClientProvider kubernetesClientProvider)
    : IFunctionEndpointSliceSource
{
    private const string ServiceNameLabel = "kubernetes.io/service-name";

    public async Task<FunctionEndpointSlicePage> ListPageAsync(
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(cancellationToken);
        var response = await kubernetes.DiscoveryV1.ListEndpointSliceForAllNamespacesAsync(
            allowWatchBookmarks: false,
            continueParameter: continuationToken,
            labelSelector: ServiceNameLabel,
            limit: 500,
            cancellationToken: cancellationToken);
        return new FunctionEndpointSlicePage(
            response.Items,
            response.Metadata?.ResourceVersion,
            response.Metadata?.ContinueProperty);
    }

    public async IAsyncEnumerable<(WatchEventType Type, V1EndpointSlice Slice)> WatchAsync(
        string resourceVersion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(cancellationToken);
        Exception? watchError = null;
        var events = kubernetes.DiscoveryV1.WatchListEndpointSliceForAllNamespacesAsync(
            allowWatchBookmarks: true,
            labelSelector: ServiceNameLabel,
            resourceVersion: resourceVersion,
            timeoutSeconds: 300,
            onError: exception => Interlocked.CompareExchange(ref watchError, exception, null),
            cancellationToken: cancellationToken);

        await foreach (var item in events.WithCancellation(cancellationToken))
            yield return item;

        if (watchError is not null)
            ExceptionDispatchInfo.Capture(watchError).Throw();
    }
}
