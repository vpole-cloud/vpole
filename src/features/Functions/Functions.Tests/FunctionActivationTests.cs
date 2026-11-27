// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using k8s.Models;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;
using Xunit;
using Xunit.Sdk;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionActivationTests
{
    private const string TargetId = "507f1f77bcf86cd799439093";

    [Fact]
    public void Endpoint_registry_projects_ready_agent_endpoints_across_slices()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey(TargetId, "functions", "orders-agent");

        registry.ApplySlice(
            TargetId,
            Slice("slice-a", service, Endpoint("10.0.0.1", ready: true), Endpoint("10.0.0.2", ready: false)));
        registry.ApplySlice(
            TargetId,
            Slice(
                "slice-b",
                service,
                Endpoint("10.0.0.1", ready: true),
                Endpoint("10.0.0.3", ready: true, terminating: true),
                Endpoint("10.0.0.4", ready: true)));

        var snapshot = registry.GetSnapshot(service);

        Assert.Equal(["10.0.0.1", "10.0.0.4"], snapshot.Endpoints.Select(endpoint => endpoint.Address));
        Assert.All(snapshot.Endpoints, endpoint => Assert.Equal(8080, endpoint.Port));

        registry.DeleteSlice(TargetId, "functions", "slice-a");

        Assert.Equal(
            ["10.0.0.1", "10.0.0.4"],
            registry.GetSnapshot(service).Endpoints.Select(endpoint => endpoint.Address));
        registry.DeleteSlice(TargetId, "functions", "slice-b");
        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);
    }

    [Fact]
    public void Endpoint_registry_rejects_non_pod_and_non_unicast_targets()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey(TargetId, "functions", "orders-agent");
        var nonPod = Endpoint("10.0.0.1", ready: true) with
        {
            TargetRef = new V1ObjectReference { Kind = "Service", Uid = "service-1" },
        };

        registry.ApplySlice(
            TargetId,
            Slice(
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
        var oldService = new FunctionServiceKey(TargetId, "functions", "old-agent");
        var newService = new FunctionServiceKey(TargetId, "functions", "new-agent");
        registry.ApplySlice(TargetId, Slice("slice", oldService, Endpoint("10.0.0.1", ready: true)));

        registry.ApplySlice(TargetId, Slice("slice", newService, Endpoint("10.0.0.2", ready: true)));

        Assert.False(registry.GetSnapshot(oldService).HasReadyEndpoints);
        Assert.Equal("10.0.0.2", Assert.Single(registry.GetSnapshot(newService).Endpoints).Address);
    }

    [Fact]
    public void Endpoint_registry_relist_removes_slices_missing_from_the_latest_list()
    {
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var service = new FunctionServiceKey(TargetId, "functions", "orders-agent");
        registry.ApplySlice(TargetId, Slice("stale", service, Endpoint("10.0.0.1", ready: true)));

        registry.ReplaceTarget(TargetId, [Slice("current", service, Endpoint("10.0.0.2", ready: true))]);

        Assert.Equal("10.0.0.2", Assert.Single(registry.GetSnapshot(service).Endpoints).Address);
        registry.DeleteSlice(TargetId, "functions", "current");
        Assert.False(registry.GetSnapshot(service).HasReadyEndpoints);
    }

    [Fact]
    public void Endpoint_registry_isolates_identical_service_identity_across_targets()
    {
        const string secondTargetId = "507f1f77bcf86cd799439094";
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var first = new FunctionServiceKey(TargetId, "functions", "orders-agent");
        var second = new FunctionServiceKey(secondTargetId, "functions", "orders-agent");

        registry.ApplySlice(TargetId, Slice("orders", first, Endpoint("10.0.0.1", ready: true)));
        registry.ApplySlice(secondTargetId, Slice("orders", second, Endpoint("10.1.0.1", ready: true)));

        Assert.Equal("10.0.0.1", Assert.Single(registry.GetSnapshot(first).Endpoints).Address);
        Assert.Equal("10.1.0.1", Assert.Single(registry.GetSnapshot(second).Endpoints).Address);

        registry.RemoveTarget(TargetId);
        Assert.False(registry.GetSnapshot(first).HasReadyEndpoints);
        Assert.True(registry.GetSnapshot(second).HasReadyEndpoints);
    }

    [Fact]
    public async Task Warm_activation_returns_snapshot_without_scaling()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        registry.ApplySlice(TargetId, Slice("ready", Service(subject), Endpoint("10.0.0.1", ready: true)));
        var coordinator = new FakeActivationCoordinator();
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, coordinator, manager, TimeProvider.System);

        var result = await activator.EnsureActiveAsync(subject);

        var snapshot = result switch
        {
            FunctionEndpointSnapshot success => success,
            FunctionRevisionActivationError error => throw new XunitException(error.Message()),
        };
        Assert.Equal("10.0.0.1", Assert.Single(snapshot.Endpoints).Address);
        Assert.Equal(0, coordinator.BeginCalls);
        Assert.Equal(0, manager.ScaleCalls);
    }

    [Fact]
    public async Task Warm_activation_forces_a_new_generation_when_admission_requires_a_fence()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        registry.ApplySlice(TargetId, Slice("ready", Service(subject), Endpoint("10.0.0.1", ready: true)));
        var coordinator = new FakeActivationCoordinator(generation: 43);
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, coordinator, manager, TimeProvider.System);

        var activation = activator.EnsureActiveAsync(subject, forceScaleFence: true);
        await manager.ScaleStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        manager.CompleteScale();
        var result = await activation;

        Assert.IsType<FunctionEndpointSnapshot>(result.Value);
        Assert.Equal(1, coordinator.BeginCalls);
        Assert.Equal(1, coordinator.CompleteCalls);
        Assert.Equal(1, manager.ScaleCalls);
        Assert.Equal(43, manager.LastGeneration);
    }

    [Fact]
    public async Task Concurrent_cold_activations_share_one_scale_operation()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var coordinator = new FakeActivationCoordinator(generation: 42);
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, coordinator, manager, TimeProvider.System);

        var first = activator.EnsureActiveAsync(subject);
        await manager.ScaleStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = activator.EnsureActiveAsync(subject);

        Assert.Equal(1, manager.ScaleCalls);
        registry.ApplySlice(TargetId, Slice("ready", Service(subject), Endpoint("10.0.0.8", ready: true)));
        manager.CompleteScale();

        var results = await Task.WhenAll(first, second);

        Assert.All(
            results,
            result =>
            {
                var snapshot = result switch
                {
                    FunctionEndpointSnapshot success => success,
                    FunctionRevisionActivationError error => throw new XunitException(error.Message()),
                };
                Assert.Equal("10.0.0.8", Assert.Single(snapshot.Endpoints).Address);
            });
        Assert.Equal(1, manager.ScaleCalls);
        Assert.Equal(42, manager.LastGeneration);
        Assert.Equal(1, coordinator.BeginCalls);
        Assert.Equal(1, coordinator.CompleteCalls);
    }

    [Fact]
    public async Task Coordination_failure_does_not_scale_the_revision()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var coordinator = new FakeActivationCoordinator(
            begin: (
                _, _,
                _) => throw new InvalidOperationException("Orleans is unavailable."));
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, coordinator, manager, TimeProvider.System);

        var result = await activator.EnsureActiveAsync(subject);

        var failure = result switch
        {
            FunctionRevisionActivationError error => error,
            FunctionEndpointSnapshot => throw new XunitException("Expected activation failure."),
        };
        var unavailable = failure switch
        {
            FunctionActivationCoordinatorUnavailable error => error,
            var error => throw new XunitException($"Expected coordinator failure, got {error.GetType().Name}."),
        };
        Assert.Contains("coordination is unavailable", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, manager.ScaleCalls);
        Assert.Equal(1, coordinator.BeginCalls);
        Assert.Equal(0, coordinator.CompleteCalls);
    }

    [Fact]
    public async Task Fenced_scale_conflict_fails_activation_without_completing_the_lease()
    {
        var subject = CreateSubject();
        var registry = new FunctionEndpointRegistry(TimeProvider.System);
        var coordinator = new FakeActivationCoordinator(generation: 17, leaseId: "shared-lease");
        var manager = new ControlledKubernetesManager();
        var activator = new FunctionRevisionActivator(registry, coordinator, manager, TimeProvider.System);

        var activation = activator.EnsureActiveAsync(subject);
        await manager.ScaleStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        manager.CompleteScale(
            new FunctionRevisionKubernetesOperationError(
                new FunctionRevisionKubernetesOperationRejected("The shared generation was already applied.")));

        var result = await activation;

        var failure = result switch
        {
            FunctionRevisionActivationError error => error,
            FunctionEndpointSnapshot => throw new XunitException("Expected activation failure."),
        };
        var scaleFailure = failure switch
        {
            FunctionActivationScaleRejected error => error,
            var error => throw new XunitException($"Expected scale failure, got {error.GetType().Name}."),
        };
        Assert.True(scaleFailure.Error is FunctionRevisionKubernetesOperationRejected);
        Assert.Equal(17, manager.LastGeneration);
        Assert.Equal(1, coordinator.BeginCalls);
        Assert.Equal(0, coordinator.CompleteCalls);
    }

    private static FunctionServiceKey Service(FunctionRevisionInKubernetes subject) => new(
        subject.Placement.DeploymentTargetId,
        subject.Namespace,
        subject.ServiceName);

    private static FunctionRevisionInKubernetes CreateSubject()
    {
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "team/functions", "v1", null, null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
            new FunctionResources(new FunctionResourceValues(100, 128), new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 5, 30),
            [
                new FunctionHandlerSpec(
                    "webhook",
                    4,
                    new HttpFunctionTriggerSpec("", "orders", ["POST"], FunctionExposure.Public)),
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "resource-group-1",
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
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
            RevisionNumber = 3,
        };
        return new FunctionRevisionInKubernetes(
            app,
            revision,
            new FunctionResourcePlacement(
                app.Placement.ResourceDeploymentId,
                app.Placement.RuntimeCellId,
                app.Placement.DeploymentTargetId,
                "pole-tenant-rg",
                "507f1f77bcf86cd799439044"));
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
                ["kubernetes.io/service-name"] = service.ServiceName,
            },
        },
        AddressType = "IPv4",
        Ports = [new Discoveryv1EndpointPort { Name = "agent", Protocol = "TCP", Port = 8080 }],
        Endpoints = endpoints,
    };

    private static V1Endpoint Endpoint(
        string address,
        bool ready,
        bool terminating = false) => new()
    {
        Addresses = [address],
        Conditions = new V1EndpointConditions { Ready = ready, Terminating = terminating },
        TargetRef = new V1ObjectReference { Kind = "Pod", Uid = $"pod-{address}" },
    };

    private sealed class ControlledKubernetesManager : IFunctionRevisionKubernetesManager
    {
        private readonly TaskCompletionSource<FunctionRevisionKubernetesMutationOutcome> scaleCompletion
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ScaleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScaleCalls { get; private set; }

        public long? LastGeneration { get; private set; }

        public Task<FunctionRevisionKubernetesMutationOutcome> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());

        public Task<FunctionRevisionKubernetesMutationOutcome> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default) => ScaleAsync(revision, replicas, 0, cancellationToken);

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default)
        {
            ScaleCalls++;
            LastGeneration = coordinationGeneration;
            ScaleStarted.TrySetResult();
            return scaleCompletion.Task;
        }

        public Task<FunctionReplicaStateOutcome> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionReplicaStateOutcome>(new FunctionReplicaState(0, 0, 0));

        public void CompleteScale(FunctionRevisionKubernetesMutationOutcome? result = null) =>
            scaleCompletion.TrySetResult(result ?? new FunctionRevisionKubernetesMutationCompleted());
    }

    private sealed class FakeActivationCoordinator(
        long generation = 1,
        string leaseId = "activation-lease",
        Func<FunctionRevisionCoordinationKey, FunctionActivationBeginRequest, CancellationToken,
            Task<FunctionActivationBeginDecision>>? begin = null
    ) : IFunctionActivationCoordinator
    {
        private int beginCalls;
        private int completeCalls;

        public int BeginCalls => Volatile.Read(ref beginCalls);

        public int CompleteCalls => Volatile.Read(ref completeCalls);

        public string? LastCompletedLeaseId { get; private set; }

        public Task<FunctionActivationBeginDecision> BeginActivationAsync(
            FunctionRevisionCoordinationKey key,
            FunctionActivationBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref beginCalls);
            if (begin is not null)
                return begin(key, request, cancellationToken);

            return Task.FromResult(
                new FunctionActivationBeginDecision(
                    FunctionActivationBeginDecisionKind.Granted,
                    new FunctionActivationLease(
                        request.OperationId,
                        leaseId,
                        generation,
                        request.DesiredReplicas,
                        DateTimeOffset.MaxValue),
                    FunctionActivationRejection.None,
                    0));
        }

        public Task<FunctionActivationCompleteDecision> CompleteActivationAsync(
            FunctionRevisionCoordinationKey key,
            FunctionActivationCompleteRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref completeCalls);
            LastCompletedLeaseId = request.LeaseId;
            return Task.FromResult(FunctionActivationCompleteDecision.Completed);
        }
    }
}
