using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Scaling;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

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
        harness.Pending.Requests = pending;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([expectedReplicas], harness.Kubernetes.ScaleRequests);
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
        harness.Pending.Requests = 1;
        harness.Pending.HandlerRequests = new Dictionary<string, int>(StringComparer.Ordinal)
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
        harness.Time.Advance(TimeSpan.FromSeconds(19));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

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
        harness.Pending.BeginningScaleDown = () => harness.Pending.Requests = 1;

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Kubernetes.ScaleRequests);
        Assert.False(harness.Pending.Quiescing);
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
        Assert.False(harness.Pending.Quiescing);
    }

    [Fact]
    public async Task Scale_down_barrier_is_held_until_deployment_converges()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 0, 30),
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.True(harness.Pending.Quiescing);
        harness.Kubernetes.ReplicaState = new FunctionReplicaState(0, 0, 0, 0);
        harness.Endpoints.Endpoints = [];
        await harness.Scaler.RunOnceAsync(CancellationToken.None);

        Assert.False(harness.Pending.Quiescing);
    }

    [Fact]
    public async Task Draining_revision_is_retired_only_after_scale_to_zero_converges()
    {
        var harness = CreateHarness(
            scaling: new FunctionScalingPolicy(0, 5, 4, 8, 10, 30),
            state: FunctionRevisionState.Draining,
            isServing: false,
            replicaState: new FunctionReplicaState(1, 1, 1, 1),
            endpoints: [Endpoint("pod-1")]);

        await harness.Scaler.RunOnceAsync(CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromSeconds(10));
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

    private static Harness CreateHarness(
        FunctionScalingPolicy scaling,
        FunctionReplicaState replicaState,
        int handlerConcurrency = 8,
        FunctionRevisionState state = FunctionRevisionState.Active,
        bool isServing = true,
        IReadOnlyList<FunctionEndpoint>? endpoints = null)
    {
        var target = CreateTarget(scaling, handlerConcurrency, state, isServing);
        var targetSource = new FakeTargetSource(target);
        var endpointRegistry = new FakeEndpointRegistry
        {
            Endpoints = endpoints?.ToImmutableArray() ?? []
        };
        var agent = new FakeAgentClient();
        var pending = new FakePendingAdmissionLimiter();
        var kubernetes = new FakeKubernetesManager { ReplicaState = replicaState };
        var time = new MutableTimeProvider(Now);
        var scaler = new FunctionScaler(
            targetSource,
            endpointRegistry,
            agent,
            pending,
            kubernetes,
            new FunctionScalerOptions { AgentStatusTimeoutSeconds = 1 },
            time,
            NullLogger<FunctionScaler>.Instance);
        return new Harness(
            target,
            targetSource,
            endpointRegistry,
            agent,
            pending,
            kubernetes,
            time,
            scaler);
    }

    private static FunctionScaleTarget CreateTarget(
        FunctionScalingPolicy scaling,
        int handlerConcurrency,
        FunctionRevisionState state,
        bool isServing)
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
            "pole-tenant-rg",
            "order-functions",
            "orders",
            spec,
            [],
            Now);
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = "507f1f77bcf86cd799439011",
            State = state,
            ReadyAtUtc = Now
        };
        var app = created.FunctionApp with
        {
            Id = revision.FunctionAppId,
            ActiveRevisionId = isServing ? revision.Id : null,
            PendingRevisionId = null,
            Status = FunctionAppStatus.Ready
        };
        return new FunctionScaleTarget(app, revision, isServing);
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
        FakePendingAdmissionLimiter Pending,
        FakeKubernetesManager Kubernetes,
        MutableTimeProvider Time,
        FunctionScaler Scaler);

    private sealed class FakeTargetSource(FunctionScaleTarget target) : IFunctionScaleTargetSource
    {
        public List<FunctionScaleTarget> Retired { get; } = [];

        public Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FunctionScaleTarget>>([target]);

        public Task<Result> MarkRetiredAsync(FunctionScaleTarget retired, CancellationToken cancellationToken)
        {
            Retired.Add(retired);
            return Task.FromResult<Result>(Result.Success());
        }
    }

    private sealed class FakeEndpointRegistry : IFunctionEndpointRegistry
    {
        public ImmutableArray<FunctionEndpoint> Endpoints { get; set; } = [];

        public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) =>
            new(service, Endpoints, Now);

        public void ReplaceAll(IEnumerable<k8s.Models.V1EndpointSlice> slices) =>
            throw new NotSupportedException();

        public void ApplySlice(k8s.Models.V1EndpointSlice slice) => throw new NotSupportedException();

        public void DeleteSlice(string @namespace, string sliceName) => throw new NotSupportedException();
    }

    private sealed class FakePendingAdmissionLimiter : IFunctionPendingAdmissionLimiter
    {
        public int Requests { get; set; }
        public bool Quiescing { get; private set; }
        public Action? BeginningScaleDown { get; set; }

        public FunctionPendingAdmissionLease? TryAcquire(
            string revisionId,
            string handlerName,
            long reservedBytes) =>
            throw new NotSupportedException();

        public FunctionPendingAdmissionSnapshot GetSnapshot(string revisionId) =>
            new(Requests, 0, Quiescing, HandlerRequests);

        public IReadOnlyDictionary<string, int> HandlerRequests { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public FunctionScaleDownLease? TryBeginScaleDown(string revisionId)
        {
            BeginningScaleDown?.Invoke();
            if (Requests != 0 || Quiescing)
                return null;
            Quiescing = true;
            return new FunctionScaleDownLease(() => Quiescing = false);
        }
    }

    private sealed class FakeKubernetesManager : IFunctionRevisionKubernetesManager
    {
        public FunctionReplicaState ReplicaState { get; set; } = new(0, 0, 0, 0);
        public List<int> ScaleRequests { get; } = [];

        public Task<Result> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(replicas);
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result<FunctionReplicaState>> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionReplicaState>>(ReplicaState);
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
