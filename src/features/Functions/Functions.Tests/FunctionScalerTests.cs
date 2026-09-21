using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Scaling;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionScalerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-17T12:00:00Z");

    [Theory]
    [InlineData(5, 3)]
    [InlineData(100, 4)]
    public async Task Pending_demand_scales_up_immediately_using_ceiling_and_maximum_clamp(
        int pending,
        int expectedReplicas)
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 4, 2, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));
        harness.Coordinator.AdmissionRequests = pending;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([expectedReplicas], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Durable_timer_demand_activates_zero_scaled_revision()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0),
            durableDemand: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["webhook"] = 3
            });

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([1], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Handler_concurrency_limit_can_require_more_instances_than_app_target()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 8, 8, 30, 30),
            handlerConcurrency: 2,
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Agent.StatusFactory = endpoint => ValidStatus(
            endpoint,
            appReserved: 4,
            handlerReserved: 4,
            handlerLimit: 4);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([2], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Pending_handler_demand_uses_handler_concurrency_limit()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 10, 10, 30, 30),
            handlerConcurrency: 1,
            replicaState: new FunctionReplicaState(2, 2, 2, 2),
            endpoints: [Endpoint("pod-1"), Endpoint("pod-2")]);
        harness.Agent.StatusFactory = endpoint => ValidStatus(
            endpoint,
            appReserved: 1,
            handlerReserved: 1,
            handlerLimit: 1);
        harness.Coordinator.AdmissionRequests = 1;
        harness.Coordinator.HandlerRequests = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["webhook"] = 1
        };

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([3], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Busy_revision_is_not_partially_scaled_down()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(4, 4, 4, 4),
            endpoints:
            [
                Endpoint("pod-1"),
                Endpoint("pod-2"),
                Endpoint("pod-3"),
                Endpoint("pod-4")
            ]);
        harness.Agent.StatusFactory = endpoint => ValidStatus(
            endpoint,
            appReserved: endpoint.TargetUid == "pod-1" ? 1 : 0);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_or_invalid_agent_sample_blocks_scale_down(bool invalidSample)
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Agent.StatusFactory = endpoint => invalidSample
            ? ValidStatus(endpoint) with { AgentInstanceId = "another-pod" }
            : null;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Endpoint_and_deployment_replica_mismatch_blocks_scale_down()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(2, 2, 2, 2),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Serving_revision_scales_to_zero_only_after_idle_delay()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 20, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Expired_ownership_reacquisition_in_same_incarnation_preserves_idle_timing()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 20, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(11));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(11));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, harness.Coordinator.OwnershipRequests.Count);
        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Scale_down_is_retried_when_deployment_returns_to_old_converged_size()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Kubernetes.ReplicaState = new FunctionReplicaState(0, 0, 0, 0);
        harness.Endpoints.Endpoints = [];
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Kubernetes.ReplicaState = new FunctionReplicaState(1, 1, 1, 1);
        harness.Endpoints.Endpoints = [Endpoint("pod-1")];
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0, 0], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Request_arriving_before_scale_down_barrier_blocks_final_scale_decision()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Coordinator.BeginningQuiescing = () => harness.Coordinator.AdmissionRequests = 1;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
        Assert.False(harness.Coordinator.Quiescing);
    }

    [Fact]
    public async Task Invocation_started_before_barrier_is_seen_by_guarded_agent_resample()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        var observations = 0;
        harness.Agent.StatusFactory = endpoint => ValidStatus(
            endpoint,
            appReserved: Interlocked.Increment(ref observations) == 1 ? 0 : 1);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
        Assert.False(harness.Coordinator.Quiescing);
    }

    [Fact]
    public async Task Scale_down_barrier_is_held_until_deployment_converges()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.True(harness.Coordinator.Quiescing);
        harness.Kubernetes.ReplicaState = new FunctionReplicaState(0, 0, 0, 0);
        harness.Endpoints.Endpoints = [];
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.False(harness.Coordinator.Quiescing);
    }

    [Fact]
    public async Task Draining_revision_is_retired_only_after_scale_to_zero_converges()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 10, 30),
            status: new DrainingFunctionRevisionStatus(Now),
            isServing: false,
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
        Assert.Empty(harness.TargetSource.Retired);

        harness.Kubernetes.ReplicaState = new FunctionReplicaState(0, 0, 0, 0);
        harness.Endpoints.Endpoints = [];
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Single(harness.TargetSource.Retired);
        Assert.Equal(harness.Target.Revision.Id, harness.TargetSource.Retired[0].Revision.Id);
    }

    [Fact]
    public async Task Serving_revision_restores_minimum_instances_without_waiting_for_idle_evidence()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(2, 5, 4, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([2], harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Scale_change_uses_coordinator_intent_observed_replicas_and_fenced_generation()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));
        harness.Coordinator.AdmissionRequests = 3;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        var begin = Assert.Single(harness.Coordinator.ScaleRequests);
        Assert.Equal(0, begin.ObservedReplicas);
        Assert.Null(begin.QuiescingEpoch);
        Assert.Equal([1], harness.Kubernetes.ScaleRequests);
        var generation = Assert.Single(harness.Kubernetes.ScaleGenerations);
        var completion = Assert.Single(harness.Coordinator.ScaleCompletions);
        Assert.Equal(generation, completion.Generation);
        Assert.Equal(FunctionCoordinationCompletionOutcome.Applied, completion.Outcome);
        Assert.Equal(begin.ScalerToken, completion.ScalerToken);
    }

    [Fact]
    public async Task Observed_replicas_classify_first_coordinated_change_as_scale_down()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Coordinator.DesiredReplicas = null;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
        var begin = Assert.Single(harness.Coordinator.ScaleRequests);
        Assert.Equal(1, begin.ObservedReplicas);
        Assert.NotNull(begin.QuiescingEpoch);
    }

    [Fact]
    public async Task Coordinator_failure_prevents_kubernetes_change()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 2, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));
        harness.Coordinator.AdmissionRequests = 4;
        harness.Coordinator.ThrowOnSnapshot = true;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.ScaleRequests);
        Assert.Empty(harness.Kubernetes.ScaleRequests);
    }

    [Fact]
    public async Task Ownership_is_renewed_and_incarnation_change_resets_idle_timing()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 20, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Coordinator.RestartIncarnation("coordinator-2");
        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
        Assert.NotEmpty(harness.Coordinator.RenewalRequests);
        Assert.Equal(2, harness.Coordinator.OwnershipRequests.Count);
        Assert.Single(harness.Coordinator.OwnershipRequests.Select(request => request.OwnerId).Distinct());
        Assert.Single(harness.Coordinator.OwnershipRequests.Select(request => request.OperationId).Distinct());
    }

    [Fact]
    public async Task Kubernetes_validation_failure_aborts_scale_intent_and_quiescing_barrier()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Kubernetes.ScaleResult = new Err<FunctionRevisionKubernetesOperationError>(
            new FunctionRevisionKubernetesOperationInvalid("Invalid replicas."));

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
        Assert.False(harness.Coordinator.Quiescing);
        Assert.Null(harness.Coordinator.PendingScaleIntent);
        Assert.Contains(
            harness.Coordinator.ScaleCompletions,
            completion => completion.Outcome == FunctionCoordinationCompletionOutcome.Failed);
        Assert.Equal(
            [FunctionQuiescingCompletionOutcome.Aborted],
            harness.Coordinator.QuiescingCompletions);
    }

    [Fact]
    public async Task Kubernetes_uncertain_failure_keeps_scale_intent_and_quiescing_for_recovery()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);
        harness.Kubernetes.ScaleResult = new Err<FunctionRevisionKubernetesOperationError>(
            new FunctionRevisionKubernetesUnavailable("Unable to scale."));

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
        Assert.True(harness.Coordinator.Quiescing);
        Assert.NotNull(harness.Coordinator.PendingScaleIntent);
        Assert.Empty(harness.Coordinator.ScaleCompletions);
        Assert.Empty(harness.Coordinator.QuiescingCompletions);

        harness.Coordinator.AdmissionRequests = 1;
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([0], harness.Kubernetes.ScaleRequests);
        Assert.True(harness.Coordinator.Quiescing);
        Assert.NotNull(harness.Coordinator.PendingScaleIntent);
        Assert.Empty(harness.Coordinator.ScaleCompletions);
        Assert.Empty(harness.Coordinator.QuiescingCompletions);
    }

    [Fact]
    public async Task Kubernetes_uncertain_failure_completes_applied_when_fenced_state_matches()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));
        harness.Coordinator.AdmissionRequests = 1;
        harness.Kubernetes.ScaleResult = new Err<FunctionRevisionKubernetesOperationError>(
            new FunctionRevisionKubernetesUnavailable("Unable to scale."));
        harness.Kubernetes.ApplyScaleBeforeFailure = true;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        var completion = Assert.Single(harness.Coordinator.ScaleCompletions);
        Assert.Equal(FunctionCoordinationCompletionOutcome.Applied, completion.Outcome);
        Assert.Null(harness.Coordinator.PendingScaleIntent);
        Assert.Equal(completion.Generation, harness.Kubernetes.ReplicaState.CoordinationGeneration);
        Assert.Equal(1, harness.Kubernetes.ReplicaState.Desired);
    }

    [Fact]
    public async Task Persisted_pending_scale_is_recovered_with_its_original_generation()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            replicaState: new FunctionReplicaState(0, 0, 0, 0));
        harness.Coordinator.SeedPendingScale(desiredReplicas: 2);
        var pending = Assert.IsType<FunctionScaleIntent>(harness.Coordinator.PendingScaleIntent);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([2], harness.Kubernetes.ScaleRequests);
        Assert.Equal([pending.Generation], harness.Kubernetes.ScaleGenerations);
        Assert.Null(harness.Coordinator.PendingScaleIntent);
        Assert.Contains(
            harness.Coordinator.ScaleCompletions,
            completion => completion.IntentId == pending.IntentId &&
                          completion.Outcome == FunctionCoordinationCompletionOutcome.Applied);
    }

    private static Harness CreateHarness(
        FunctionScalingPolicy scaling,
        FunctionReplicaState replicaState,
        int handlerConcurrency = 8,
        FunctionRevisionStatus? status = null,
        bool isServing = true,
        IReadOnlyList<FunctionEndpoint>? endpoints = null,
        IReadOnlyDictionary<string, int>? durableDemand = null)
    {
        var target = CreateTarget(scaling, handlerConcurrency, status, isServing, durableDemand);
        var targetSource = new FakeTargetSource(target);
        var endpointRegistry = new FakeEndpointRegistry
        {
            Endpoints = endpoints?.ToImmutableArray() ?? []
        };
        var time = new MutableTimeProvider(Now);
        var agent = new FakeAgentClient();
        var coordinator = new FakeScalingCoordinator(time)
        {
            DesiredReplicas = replicaState.Desired
        };
        var kubernetes = new FakeKubernetesManager { ReplicaState = replicaState };
        var scaler = new FunctionScaler(
            targetSource,
            endpointRegistry,
            agent,
            coordinator,
            new FixedPlacementResolver(),
            kubernetes,
            new FunctionScalerOptions { AgentStatusTimeoutSeconds = 1 },
            time,
            NullLogger<FunctionScaler>.Instance);
        return new Harness(
            target,
            targetSource,
            endpointRegistry,
            agent,
            coordinator,
            kubernetes,
            time,
            scaler);
    }

    private static FunctionScaleTarget CreateTarget(
        FunctionScalingPolicy scaling,
        int handlerConcurrency,
        FunctionRevisionStatus? status,
        bool isServing,
        IReadOnlyDictionary<string, int>? durableDemand)
    {
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "team/functions", "v1"),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            scaling,
            [
                new FunctionHandlerSpec(
                    "webhook",
                    handlerConcurrency,
                    new HttpFunctionTriggerSpec(
                        string.Empty,
                        "orders",
                        ["POST"],
                        FunctionExposure.Public))
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
            Now);
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = "507f1f77bcf86cd799439011",
            Status = status ?? new ActiveFunctionRevisionStatus(Now)
        };
        var app = created.FunctionApp with
        {
            Id = revision.FunctionAppId,
            ActiveRevisionId = isServing ? revision.Id : null,
            PendingRevisionId = null,
            Status = FunctionAppStatus.Ready
        };
        return new FunctionScaleTarget(
            app,
            revision,
            isServing,
            durableDemand ?? new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private static FunctionEndpoint Endpoint(string podUid) =>
        new($"10.0.0.{podUid[^1]}", 8080, podUid);

    private static FunctionAgentStatusResponse ValidStatus(
        FunctionEndpoint endpoint,
        int appReserved = 0,
        int handlerReserved = 0,
        int handlerLimit = 8) =>
        new(
            PoleFunctionProtocol.Version,
            endpoint.TargetUid!,
            FunctionAgentLifecycleState.Accepting,
            new FunctionCapacityStatus(8, appReserved, 0, 8 - appReserved),
            [
                new FunctionHandlerCapacityStatus(
                    "webhook",
                    new FunctionCapacityStatus(
                        handlerLimit,
                        handlerReserved,
                        0,
                        handlerLimit - handlerReserved))
            ],
            [],
            false,
            Now);

    private sealed record Harness(
        FunctionScaleTarget Target,
        FakeTargetSource TargetSource,
        FakeEndpointRegistry Endpoints,
        FakeAgentClient Agent,
        FakeScalingCoordinator Coordinator,
        FakeKubernetesManager Kubernetes,
        MutableTimeProvider Time,
        FunctionScaler Scaler);

    private sealed class FakeTargetSource(FunctionScaleTarget target) : IFunctionScaleTargetSource
    {
        public List<FunctionScaleTarget> Retired { get; } = [];

        public Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FunctionScaleTarget>>([target]);

        public Task<FunctionRevisionLifecycleOutcome> MarkRetiredAsync(
            FunctionScaleTarget retired,
            CancellationToken cancellationToken)
        {
            Retired.Add(retired);
            return Task.FromResult<FunctionRevisionLifecycleOutcome>(
                new FunctionRevisionLifecycleCompleted());
        }
    }

    private sealed class FakeEndpointRegistry : IFunctionEndpointRegistry
    {
        public ImmutableArray<FunctionEndpoint> Endpoints { get; set; } = [];

        public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) =>
            new(service, Endpoints, Now);

        public void ReplaceTarget(string deploymentTargetId, IEnumerable<k8s.Models.V1EndpointSlice> slices) =>
            throw new NotSupportedException();

        public void ApplySlice(string deploymentTargetId, k8s.Models.V1EndpointSlice slice) => throw new NotSupportedException();

        public void DeleteSlice(string deploymentTargetId, string @namespace, string sliceName) => throw new NotSupportedException();

        public void RemoveTarget(string deploymentTargetId) => throw new NotSupportedException();
    }

    private sealed class FixedPlacementResolver : IFunctionResourcePlacementResolver
    {
        public Task<Result<FunctionResourcePlacement, FunctionResourcePlacementUnavailable>> ResolveAsync(
            FunctionApp app,
            bool requireReady = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionResourcePlacement, FunctionResourcePlacementUnavailable>>(
                new Ok<FunctionResourcePlacement>(new FunctionResourcePlacement(
                    app.Placement.ResourceDeploymentId,
                    app.Placement.RuntimeCellId,
                    app.Placement.DeploymentTargetId,
                    "pole-tenant-rg",
                    "507f1f77bcf86cd799439044")));
    }

    private sealed class FakeScalingCoordinator(MutableTimeProvider time) : IFunctionScalingCoordinator
    {
        private FunctionScalerOwnershipToken? ownership;
        private int ownershipSequence;
        private int intentSequence;

        public string Incarnation { get; set; } = "coordinator-1";
        public long Generation { get; set; } = 10;
        public int? DesiredReplicas { get; set; }
        public int AdmissionRequests { get; set; }
        public IReadOnlyDictionary<string, int> HandlerRequests { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public FunctionQuiescingIntent? QuiescingIntent { get; private set; }
        public FunctionScaleIntent? PendingScaleIntent { get; private set; }
        public bool Quiescing => QuiescingIntent is not null;
        public bool RejectOwnership { get; set; }
        public bool RejectRenewal { get; set; }
        public bool ThrowOnSnapshot { get; set; }
        public bool ThrowOnBeginScale { get; set; }
        public Action? BeginningQuiescing { get; set; }
        public List<FunctionScalerOwnershipRequest> OwnershipRequests { get; } = [];
        public List<FunctionScalerOwnershipToken> RenewalRequests { get; } = [];
        public List<FunctionScaleBeginRequest> ScaleRequests { get; } = [];
        public List<FunctionScaleCompleteRequest> ScaleCompletions { get; } = [];
        public List<FunctionQuiescingCompletionOutcome> QuiescingCompletions { get; } = [];

        public Task<FunctionScalerOwnershipDecision> TryAcquireOwnershipAsync(
            FunctionRevisionCoordinationKey key,
            FunctionScalerOwnershipRequest request,
            CancellationToken cancellationToken = default)
        {
            OwnershipRequests.Add(request);
            if (RejectOwnership)
                return Task.FromResult(new FunctionScalerOwnershipDecision(
                    FunctionScalerOwnershipDecisionKind.Busy,
                    null,
                    1));

            if (ownership is not null &&
                ownership.ExpiresAtUtc > time.GetUtcNow() &&
                (!string.Equals(ownership.OperationId, request.OperationId, StringComparison.Ordinal) ||
                 !string.Equals(ownership.OwnerId, request.OwnerId, StringComparison.Ordinal)))
            {
                return Task.FromResult(new FunctionScalerOwnershipDecision(
                    FunctionScalerOwnershipDecisionKind.Busy,
                    null,
                    1));
            }

            ownership = ownership is not null &&
                        ownership.ExpiresAtUtc > time.GetUtcNow() &&
                        string.Equals(ownership.OperationId, request.OperationId, StringComparison.Ordinal) &&
                        string.Equals(ownership.OwnerId, request.OwnerId, StringComparison.Ordinal)
                ? ownership
                : new FunctionScalerOwnershipToken(
                    request.OperationId,
                    request.OwnerId,
                    $"ownership-{++ownershipSequence}",
                    Incarnation,
                    time.GetUtcNow().AddSeconds(10));
            return Task.FromResult(new FunctionScalerOwnershipDecision(
                FunctionScalerOwnershipDecisionKind.Granted,
                ownership,
                0));
        }

        public Task<FunctionScalerOwnershipDecision> RenewOwnershipAsync(
            FunctionRevisionCoordinationKey key,
            FunctionScalerOwnershipToken token,
            CancellationToken cancellationToken = default)
        {
            RenewalRequests.Add(token);
            if (RejectRenewal || !Owns(token))
            {
                return Task.FromResult(new FunctionScalerOwnershipDecision(
                    FunctionScalerOwnershipDecisionKind.Rejected,
                    null,
                    0));
            }

            ownership = ownership! with { ExpiresAtUtc = time.GetUtcNow().AddSeconds(10) };
            return Task.FromResult(new FunctionScalerOwnershipDecision(
                FunctionScalerOwnershipDecisionKind.Granted,
                ownership,
                0));
        }

        public Task<FunctionScalerOwnershipReleaseDecision> ReleaseOwnershipAsync(
            FunctionRevisionCoordinationKey key,
            FunctionScalerOwnershipToken token,
            CancellationToken cancellationToken = default)
        {
            if (!Owns(token))
                return Task.FromResult(FunctionScalerOwnershipReleaseDecision.Stale);
            ownership = null;
            return Task.FromResult(FunctionScalerOwnershipReleaseDecision.Released);
        }

        public Task<FunctionQuiescingDecision> BeginQuiescingAsync(
            FunctionRevisionCoordinationKey key,
            FunctionQuiescingBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            BeginningQuiescing?.Invoke();
            if (!Owns(request.ScalerToken))
            {
                return Task.FromResult(new FunctionQuiescingDecision(
                    FunctionQuiescingDecisionKind.Stale,
                    null,
                    0));
            }
            if (AdmissionRequests != 0)
            {
                return Task.FromResult(new FunctionQuiescingDecision(
                    FunctionQuiescingDecisionKind.ActiveAdmissions,
                    null,
                    1));
            }
            if (QuiescingIntent is not null &&
                !string.Equals(QuiescingIntent.OperationId, request.OperationId, StringComparison.Ordinal))
            {
                return Task.FromResult(new FunctionQuiescingDecision(
                    FunctionQuiescingDecisionKind.Busy,
                    null,
                    1));
            }

            QuiescingIntent ??= new FunctionQuiescingIntent(
                request.OperationId,
                $"quiescing-{++intentSequence}",
                ++Generation,
                time.GetUtcNow(),
                time.GetUtcNow().AddMinutes(5));
            return Task.FromResult(new FunctionQuiescingDecision(
                FunctionQuiescingDecisionKind.Granted,
                new FunctionQuiescingLease(
                    QuiescingIntent.OperationId,
                    QuiescingIntent.LeaseId,
                    QuiescingIntent.Epoch,
                    QuiescingIntent.ExpiresAtUtc),
                0));
        }

        public Task<FunctionQuiescingCompleteDecision> CompleteQuiescingAsync(
            FunctionRevisionCoordinationKey key,
            FunctionQuiescingCompleteRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!Owns(request.ScalerToken) ||
                QuiescingIntent is null ||
                QuiescingIntent.LeaseId != request.LeaseId ||
                QuiescingIntent.Epoch != request.Epoch)
            {
                return Task.FromResult(FunctionQuiescingCompleteDecision.Stale);
            }
            if (PendingScaleIntent is not null)
                return Task.FromResult(FunctionQuiescingCompleteDecision.Busy);
            if (AdmissionRequests != 0)
                return Task.FromResult(FunctionQuiescingCompleteDecision.ActiveAdmissions);

            QuiescingCompletions.Add(request.Outcome);
            QuiescingIntent = null;
            if (request.Outcome == FunctionQuiescingCompletionOutcome.Aborted)
                Generation++;
            return Task.FromResult(FunctionQuiescingCompleteDecision.Completed);
        }

        public Task<FunctionScaleBeginDecision> BeginScaleAsync(
            FunctionRevisionCoordinationKey key,
            FunctionScaleBeginRequest request,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(request);
            if (ThrowOnBeginScale)
                throw new InvalidOperationException("Coordinator unavailable.");
            if (!Owns(request.ScalerToken))
                return Task.FromResult(ScaleDecision(FunctionScaleBeginDecisionKind.Stale));
            if (PendingScaleIntent is not null)
            {
                if (!string.Equals(PendingScaleIntent.OperationId, request.OperationId, StringComparison.Ordinal) ||
                    PendingScaleIntent.DesiredReplicas != request.DesiredReplicas ||
                    PendingScaleIntent.QuiescingEpoch != request.QuiescingEpoch)
                {
                    return Task.FromResult(ScaleDecision(FunctionScaleBeginDecisionKind.Busy));
                }

                return Task.FromResult(new FunctionScaleBeginDecision(
                    FunctionScaleBeginDecisionKind.Granted,
                    ToLease(PendingScaleIntent),
                    0));
            }

            var isScaleDown = request.DesiredReplicas < Math.Max(DesiredReplicas ?? 0, request.ObservedReplicas);
            if (isScaleDown &&
                (request.QuiescingEpoch is null || QuiescingIntent?.Epoch != request.QuiescingEpoch))
            {
                return Task.FromResult(ScaleDecision(FunctionScaleBeginDecisionKind.QuiescingRequired));
            }
            if (!isScaleDown && request.QuiescingEpoch is not null)
                return Task.FromResult(ScaleDecision(FunctionScaleBeginDecisionKind.Rejected));

            var previous = DesiredReplicas;
            DesiredReplicas = request.DesiredReplicas;
            PendingScaleIntent = new FunctionScaleIntent(
                request.OperationId,
                $"scale-{++intentSequence}",
                ++Generation,
                request.DesiredReplicas,
                previous,
                request.QuiescingEpoch,
                time.GetUtcNow(),
                time.GetUtcNow().AddMinutes(5));
            return Task.FromResult(new FunctionScaleBeginDecision(
                FunctionScaleBeginDecisionKind.Granted,
                ToLease(PendingScaleIntent),
                0));
        }

        public Task<FunctionScaleCompleteDecision> CompleteScaleAsync(
            FunctionRevisionCoordinationKey key,
            FunctionScaleCompleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ScaleCompletions.Add(request);
            if (!Owns(request.ScalerToken) ||
                PendingScaleIntent is null ||
                PendingScaleIntent.IntentId != request.IntentId ||
                PendingScaleIntent.Generation != request.Generation)
            {
                return Task.FromResult(FunctionScaleCompleteDecision.Stale);
            }

            if (request.Outcome == FunctionCoordinationCompletionOutcome.Failed)
                DesiredReplicas = PendingScaleIntent.PreviousDesiredReplicas;
            PendingScaleIntent = null;
            return Task.FromResult(FunctionScaleCompleteDecision.Completed);
        }

        public Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync(
            FunctionRevisionCoordinationKey key,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSnapshot)
                throw new InvalidOperationException("Coordinator unavailable.");
            return Task.FromResult(new FunctionRevisionCoordinatorSnapshot(
                key,
                Incarnation,
                Generation,
                DesiredReplicas,
                DateTimeOffset.MinValue,
                new FunctionAdmissionSnapshot(
                    AdmissionRequests,
                    0,
                    new Dictionary<string, int>(HandlerRequests, StringComparer.Ordinal)),
                null,
                QuiescingIntent,
                PendingScaleIntent,
                ownership));
        }

        public void RestartIncarnation(string incarnation)
        {
            Incarnation = incarnation;
            ownership = null;
        }

        public void SeedPendingScale(int desiredReplicas, long? quiescingEpoch = null)
        {
            var previous = DesiredReplicas;
            if (quiescingEpoch is not null && QuiescingIntent is null)
            {
                QuiescingIntent = new FunctionQuiescingIntent(
                    "recovered-quiescing",
                    "recovered-quiescing-lease",
                    quiescingEpoch.Value,
                    time.GetUtcNow(),
                    time.GetUtcNow().AddMinutes(5));
            }
            DesiredReplicas = desiredReplicas;
            PendingScaleIntent = new FunctionScaleIntent(
                "recovered-scale",
                "recovered-intent",
                ++Generation,
                desiredReplicas,
                previous,
                quiescingEpoch,
                time.GetUtcNow(),
                time.GetUtcNow().AddMinutes(5));
        }

        private bool Owns(FunctionScalerOwnershipToken token) =>
            ownership is not null &&
            ownership.ExpiresAtUtc > time.GetUtcNow() &&
            string.Equals(ownership.OperationId, token.OperationId, StringComparison.Ordinal) &&
            string.Equals(ownership.OwnerId, token.OwnerId, StringComparison.Ordinal) &&
            string.Equals(ownership.LeaseId, token.LeaseId, StringComparison.Ordinal) &&
            string.Equals(ownership.CoordinatorIncarnation, token.CoordinatorIncarnation, StringComparison.Ordinal) &&
            string.Equals(token.CoordinatorIncarnation, Incarnation, StringComparison.Ordinal);

        private static FunctionScaleBeginDecision ScaleDecision(FunctionScaleBeginDecisionKind kind) =>
            new(kind, null, 0);

        private static FunctionScaleLease ToLease(FunctionScaleIntent intent) =>
            new(
                intent.OperationId,
                intent.IntentId,
                intent.Generation,
                intent.DesiredReplicas,
                intent.ExpiresAtUtc);
    }

    private sealed class FakeKubernetesManager : IFunctionRevisionKubernetesManager
    {
        public FunctionReplicaState ReplicaState { get; set; } = new(0, 0, 0, 0);
        public List<int> ScaleRequests { get; } = [];
        public List<long> ScaleGenerations { get; } = [];
        public Result<Unit, FunctionRevisionKubernetesOperationError> ScaleResult { get; set; } =
            new Ok<Unit>(Unit.Value);
        public bool ApplyScaleBeforeFailure { get; set; }

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The scaler must use a fenced coordination generation.");

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(replicas);
            ScaleGenerations.Add(coordinationGeneration);
            if (ScaleResult is Ok<Unit> || ApplyScaleBeforeFailure)
            {
                ReplicaState = ReplicaState with
                {
                    Desired = replicas,
                    GenerationObserved = false,
                    CoordinationGeneration = coordinationGeneration
                };
            }
            return Task.FromResult(ScaleResult);
        }

        public Task<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>>(
                new Ok<FunctionReplicaState>(ReplicaState));
    }

    private sealed class FakeAgentClient : IFunctionAgentClient
    {
        public Func<FunctionEndpoint, FunctionAgentStatusResponse?> StatusFactory { get; set; } =
            endpoint => ValidStatus(endpoint);

        public Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken)
        {
            var status = StatusFactory(endpoint);
            return Task.FromResult(status is null
                ? new FunctionAgentProtocolResponse<FunctionAgentStatusResponse>(
                    HttpStatusCode.ServiceUnavailable,
                    null,
                    new FunctionProtocolError("unavailable", "Unavailable", true))
                : new FunctionAgentProtocolResponse<FunctionAgentStatusResponse>(
                    HttpStatusCode.OK,
                    status,
                    null));
        }

        public Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            ReserveFunctionAttemptRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            ExecuteFunctionAttemptRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            int waitUpToMilliseconds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
