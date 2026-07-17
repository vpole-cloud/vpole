using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Functions.Runtime.Endpoints;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionEndpointSliceWatcherTests
{
    [Fact]
    public async Task Initial_relist_atomically_replaces_stale_registry_state()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var staleService = new FunctionServiceKey("functions", "fn-stale-r1-agent");
        var currentService = new FunctionServiceKey("functions", "fn-current-r2-agent");
        registry.ApplySlice(Slice("stale", staleService, "10.0.0.1", "1"));
        var source = new FakeEndpointSliceSource(
            [Slice("current", currentService, "10.0.0.2", "7")]);
        var watcher = new FunctionEndpointSliceWatcher(
            source,
            registry,
            TimeProvider.System,
            NullLogger<FunctionEndpointSliceWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        await source.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(registry.GetSnapshot(staleService).HasReadyEndpoints);
        Assert.Equal("10.0.0.2", Assert.Single(registry.GetSnapshot(currentService).Endpoints).Address);

        await watcher.StopAsync(CancellationToken.None);
        Assert.Equal("7", source.WatchedResourceVersion);
    }

    [Fact]
    public async Task Watch_applies_added_modified_and_deleted_events()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey("functions", "fn-orders-r1-agent");
        var source = new FakeEndpointSliceSource(
            [],
            (WatchEventType.Added, Slice("orders", service, "10.0.0.1", "2")),
            (WatchEventType.Modified, Slice("orders", service, "10.0.0.2", "3")),
            (WatchEventType.Deleted, Slice("orders", service, "10.0.0.2", "4")));
        var watcher = new FunctionEndpointSliceWatcher(
            source,
            registry,
            TimeProvider.System,
            NullLogger<FunctionEndpointSliceWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        await source.EventsDelivered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);

        await watcher.StopAsync(CancellationToken.None);
    }

    private static V1EndpointSlice Slice(
        string name,
        FunctionServiceKey service,
        string address,
        string resourceVersion) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = service.Namespace,
            ResourceVersion = resourceVersion,
            Labels = new Dictionary<string, string>
            {
                ["kubernetes.io/service-name"] = service.ServiceName
            }
        },
        AddressType = "IPv4",
        Ports = [new Discoveryv1EndpointPort { Name = "agent", Protocol = "TCP", Port = 8080 }],
        Endpoints =
        [
            new V1Endpoint
            {
                Addresses = [address],
                Conditions = new V1EndpointConditions { Ready = true, Terminating = false },
                TargetRef = new V1ObjectReference { Kind = "Pod", Uid = $"pod-{address}" }
            }
        ]
    };

    private sealed class FakeEndpointSliceSource(
        IList<V1EndpointSlice> initial,
        params (WatchEventType Type, V1EndpointSlice Slice)[] events)
        : IFunctionEndpointSliceSource
    {
        public TaskCompletionSource WatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EventsDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? WatchedResourceVersion { get; private set; }

        public Task<FunctionEndpointSlicePage> ListPageAsync(
            string? continuationToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FunctionEndpointSlicePage(initial, "7", null));

        public async IAsyncEnumerable<(WatchEventType Type, V1EndpointSlice Slice)> WatchAsync(
            string resourceVersion,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WatchedResourceVersion = resourceVersion;
            WatchStarted.TrySetResult();
            foreach (var item in events)
                yield return item;
            EventsDelivered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
