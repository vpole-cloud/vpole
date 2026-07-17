using System.Collections.Immutable;
using k8s.Models;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionActivationTests
{
    [Fact]
    public void Endpoint_registry_projects_ready_agent_endpoints_across_slices()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey("functions", "orders-agent");

        registry.ApplySlice(Slice(
            "slice-a",
            service,
            Endpoint("10.0.0.1", ready: true),
            Endpoint("10.0.0.2", ready: false)));
        registry.ApplySlice(Slice(
            "slice-b",
            service,
            Endpoint("10.0.0.1", ready: true),
            Endpoint("10.0.0.3", ready: true, terminating: true),
            Endpoint("10.0.0.4", ready: true)));

        var snapshot = registry.GetSnapshot(service);

        Assert.Equal(["10.0.0.1", "10.0.0.4"], snapshot.Endpoints.Select(endpoint => endpoint.Address));
        Assert.All(snapshot.Endpoints, endpoint => Assert.Equal(8080, endpoint.Port));

        registry.DeleteSlice("functions", "slice-a");

        Assert.Equal(
            ["10.0.0.1", "10.0.0.4"],
            registry.GetSnapshot(service).Endpoints.Select(endpoint => endpoint.Address));
        registry.DeleteSlice("functions", "slice-b");
        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);
    }

    [Fact]
    public void Endpoint_registry_rejects_non_pod_and_non_unicast_targets()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey("functions", "orders-agent");
        var nonPod = Endpoint("10.0.0.1", ready: true) with
        {
            TargetRef = new V1ObjectReference { Kind = "Service", Uid = "service-1" }
        };

        registry.ApplySlice(Slice(
            "slice-untrusted",
            service,
            nonPod,
            Endpoint("127.0.0.1", ready: true),
            Endpoint("169.254.10.2", ready: true),
            Endpoint("not-an-ip", ready: true)));

        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);
    }

    [Fact]
    public void Endpoint_registry_rebuilds_old_service_when_a_slice_changes_ownership()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var oldService = new FunctionServiceKey("functions", "old-agent");
        var newService = new FunctionServiceKey("functions", "new-agent");
        registry.ApplySlice(Slice("slice", oldService, Endpoint("10.0.0.1", ready: true)));

        registry.ApplySlice(Slice("slice", newService, Endpoint("10.0.0.2", ready: true)));

        Assert.False(registry.GetSnapshot(oldService).HasReadyEndpoints);
        Assert.Equal("10.0.0.2", Assert.Single(registry.GetSnapshot(newService).Endpoints).Address);
    }

    [Fact]
    public void Endpoint_registry_relist_removes_slices_missing_from_the_latest_list()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey("functions", "orders-agent");
        registry.ApplySlice(Slice("stale", service, Endpoint("10.0.0.1", ready: true)));

        registry.ReplaceAll([Slice("current", service, Endpoint("10.0.0.2", ready: true))]);

        Assert.Equal("10.0.0.2", Assert.Single(registry.GetSnapshot(service).Endpoints).Address);
        registry.DeleteSlice("functions", "current");
        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);
    }

    [Fact]
    public async Task Warm_activation_returns_snapshot_without_scaling()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        registry.ApplySlice(Slice(
            "ready",
            new FunctionServiceKey(subject.Namespace, subject.ServiceName),
            Endpoint("10.0.0.1", ready: true)));
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, manager, TimeProvider.System);

        var result = await activator.EnsureActiveAsync(subject.App, subject.Revision);

        var success = Assert.IsType<Success<FunctionEndpointSnapshot>>(result);
        Assert.Equal("10.0.0.1", Assert.Single(success.Value.Endpoints).Address);
        Assert.Equal(0, manager.ScaleCalls);
    }

    [Fact]
    public async Task Concurrent_cold_activations_share_one_scale_operation()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, manager, TimeProvider.System);

        var first = activator.EnsureActiveAsync(subject.App, subject.Revision);
        await manager.ScaleStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = activator.EnsureActiveAsync(subject.App, subject.Revision);

        Assert.Equal(1, manager.ScaleCalls);
        registry.ApplySlice(Slice(
            "ready",
            new FunctionServiceKey(subject.Namespace, subject.ServiceName),
            Endpoint("10.0.0.8", ready: true)));
        manager.CompleteScale();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
        {
            var success = Assert.IsType<Success<FunctionEndpointSnapshot>>(result);
            Assert.Equal("10.0.0.8", Assert.Single(success.Value.Endpoints).Address);
        });
        Assert.Equal(1, manager.ScaleCalls);
    }

    private static FunctionRevisionInKubernetes CreateSubject()
    {
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "team/functions", "v1", null, null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 5, 30),
            [
                new FunctionHandlerSpec(
                    "webhook",
                    4,
                    new HttpFunctionTriggerSpec(
                        string.Empty,
                        "orders",
                        ["POST"],
                        FunctionExposure.Public))
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "resource-group-1",
            "pole-tenant-rg",
            "order-functions",
            "orders",
            spec,
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));
        var app = created.FunctionApp with { Id = "507f1f77bcf86cd799439011" };
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = app.Id,
            RevisionNumber = 3
        };
        return new FunctionRevisionInKubernetes(app, revision);
    }

    private static V1EndpointSlice Slice(
        string name,
        FunctionServiceKey service,
        params V1Endpoint[] endpoints) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = service.Namespace,
            Labels = new Dictionary<string, string>
            {
                ["kubernetes.io/service-name"] = service.ServiceName
            }
        },
        AddressType = "IPv4",
        Ports = [new Discoveryv1EndpointPort { Name = "agent", Protocol = "TCP", Port = 8080 }],
        Endpoints = endpoints
    };

    private static V1Endpoint Endpoint(string address, bool ready, bool terminating = false) => new()
    {
        Addresses = [address],
        Conditions = new V1EndpointConditions { Ready = ready, Terminating = terminating },
        TargetRef = new V1ObjectReference { Kind = "Pod", Uid = $"pod-{address}" }
    };

    private sealed class ControlledKubernetesManager : IFunctionRevisionKubernetesManager
    {
        private readonly TaskCompletionSource<Result> scaleCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ScaleStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScaleCalls { get; private set; }

        public Task<Result> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            ScaleCalls++;
            ScaleStarted.TrySetResult();
            return scaleCompletion.Task;
        }

        public void CompleteScale() => scaleCompletion.TrySetResult(Result.Success());
    }
}
