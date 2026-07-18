using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Timers;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionInvocationDispatcherTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:10:30Z");
    private static readonly FunctionEndpoint FirstEndpoint = new("10.42.0.11", 8080, "pod-1");
    private static readonly FunctionEndpoint SecondEndpoint = new("10.42.0.12", 8080, "pod-2");

    [Fact]
    public async Task Successful_timer_dispatch_persists_running_before_succeeded_and_deterministic_meter_events()
    {
        var subject = CreateSubject();
        var invocation = CreateInvocation(subject);
        await InsertAsync(subject, invocation);
        var observedRunning = false;
        var agent = new RecordingAgentClient
        {
            ObserveHandler = async (endpoint, attemptId) =>
            {
                var persisted = await DbContext.FunctionInvocations
                    .Find(candidate => candidate.Id == invocation.Id)
                    .SingleAsync();
                var startedMeter = await DbContext.FunctionMeterEvents
                    .Find(candidate => candidate.InvocationId == invocation.Id &&
                                       candidate.Phase == FunctionMeterEventPhase.Started)
                    .SingleAsync();
                observedRunning = persisted.State == FunctionInvocationState.Running &&
                                  startedMeter.Id == MeterId(invocation.Id, 1, FunctionMeterEventPhase.Started);
                return SuccessfulCompletion(endpoint, attemptId);
            }
        };

        await CreateDispatcher(agent).RunOnceAsync(CancellationToken.None);

        Assert.True(observedRunning);
        var persisted = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == invocation.Id)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.Succeeded, persisted.State);
        Assert.Equal(Now.AddSeconds(2), persisted.CompletedAtUtc);
        Assert.Null(persisted.Lease);

        var meterEvents = await DbContext.FunctionMeterEvents
            .Find(candidate => candidate.InvocationId == invocation.Id)
            .SortBy(candidate => candidate.Phase)
            .ToListAsync();
        Assert.Collection(
            meterEvents,
            started =>
            {
                Assert.Equal(MeterId(invocation.Id, 1, FunctionMeterEventPhase.Started), started.Id);
                Assert.Equal(FunctionMeterEventPhase.Started, started.Phase);
                Assert.Null(started.Outcome);
                Assert.Null(started.ExecutionDurationMilliseconds);
                Assert.Equal(Now, started.OccurredAtUtc);
            },
            completed =>
            {
                Assert.Equal(MeterId(invocation.Id, 1, FunctionMeterEventPhase.Completed), completed.Id);
                Assert.Equal(FunctionMeterEventPhase.Completed, completed.Phase);
                Assert.Equal(FunctionMeterOutcome.Succeeded, completed.Outcome);
                Assert.Equal(2000, completed.ExecutionDurationMilliseconds);
                Assert.Equal(Now.AddSeconds(2), completed.OccurredAtUtc);
            });

        var payload = Assert.IsType<TimerFunctionInvocationPayload>(Assert.Single(agent.ExecuteRequests).Payload);
        Assert.Equal(subject.Trigger.TriggerId, payload.TriggerId);
        Assert.Equal(subject.Trigger.ScheduleVersion, payload.ScheduleVersion);
        Assert.Equal(invocation.Id, payload.OccurrenceId);
        Assert.Equal(invocation.ScheduledForUtc, payload.ScheduledForUtc);
        Assert.Equal(Now, payload.FiredAtUtc);
        Assert.Equal(invocation.IsPastDue, payload.IsPastDue);
        Assert.Equal(invocation.MissedOccurrenceCount, payload.MissedOccurrenceCount);
    }

    [Fact]
    public async Task Retryable_agent_completion_schedules_retry_and_records_attempt_outcome()
    {
        var subject = CreateSubject();
        var invocation = CreateInvocation(subject);
        await InsertAsync(subject, invocation);
        var agent = new RecordingAgentClient
        {
            ObserveHandler = (endpoint, attemptId) => Task.FromResult(new FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>(
                HttpStatusCode.OK,
                new FunctionAttemptStatusResponse(
                    endpoint.TargetUid!,
                    Now.AddSeconds(2),
                    new CompletedFunctionAttemptStatus(
                        attemptId,
                        Now,
                        Now.AddSeconds(2),
                        FunctionAttemptOutcome.RetryableFailure,
                        Result: null,
                        Failure: new FunctionAttemptFailure("dependency_busy", "Try again.", FunctionFailureOrigin.User))),
                Error: null))
        };

        await CreateDispatcher(agent).RunOnceAsync(CancellationToken.None);

        var persisted = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == invocation.Id)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.RetryScheduled, persisted.State);
        Assert.Equal(1, persisted.AttemptNumber);
        Assert.Equal(Now.AddSeconds(7), persisted.NextAttemptAtUtc);
        Assert.Equal("dependency_busy", persisted.Error?.Code);
        Assert.True(persisted.Error?.Retryable is true);
        Assert.Null(persisted.Lease);

        var completed = await DbContext.FunctionMeterEvents
            .Find(candidate => candidate.InvocationId == invocation.Id &&
                               candidate.Phase == FunctionMeterEventPhase.Completed)
            .SingleAsync();
        Assert.Equal(FunctionMeterOutcome.RetryableFailure, completed.Outcome);
        Assert.Equal(2000, completed.ExecutionDurationMilliseconds);
    }

    [Theory]
    [InlineData(FunctionInvocationState.Dispatching)]
    [InlineData(FunctionInvocationState.Running)]
    public async Task Expired_attempt_lease_is_recovered_without_calling_an_agent(FunctionInvocationState state)
    {
        var subject = CreateSubject();
        var invocation = CreateInvocation(subject, retry: new FunctionRetryPolicy(1, []));
        var leased = invocation.AcquireDispatchLease(
            "abandoned-dispatcher",
            "expired-lease",
            Now.AddMinutes(-2),
            Now.AddMinutes(-1));
        if (state == FunctionInvocationState.Running)
            leased = leased.MarkRunning("expired-lease", Now.AddSeconds(-90));
        await InsertAsync(subject, leased);
        var agent = new RecordingAgentClient();

        await CreateDispatcher(agent).RunOnceAsync(CancellationToken.None);

        var recovered = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == invocation.Id)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.Failed, recovered.State);
        Assert.Equal("attempt_lease_expired", recovered.Error?.Code);
        Assert.True(recovered.Error?.Retryable is true);
        Assert.Equal(Now, recovered.CompletedAtUtc);
        Assert.Null(recovered.Lease);
        Assert.Empty(agent.ReservedEndpoints);
        Assert.Empty(agent.ExecutedEndpoints);
        var meters = await DbContext.FunctionMeterEvents.Find(candidate => candidate.InvocationId == invocation.Id)
            .ToListAsync();
        if (state == FunctionInvocationState.Running)
        {
            var completed = Assert.Single(meters);
            Assert.Equal(FunctionMeterEventPhase.Completed, completed.Phase);
            Assert.Equal(FunctionMeterOutcome.PlatformFailure, completed.Outcome);
        }
        else
        {
            Assert.Empty(meters);
        }
    }

    [Fact]
    public async Task Cross_tenant_app_and_revision_references_fail_permanently()
    {
        var subject = CreateSubject();
        var invocation = CreateInvocation(subject, tenantId: "tenant-2");
        await InsertAsync(subject, invocation);
        var agent = new RecordingAgentClient();

        await CreateDispatcher(agent).RunOnceAsync(CancellationToken.None);

        var failed = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == invocation.Id)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.Failed, failed.State);
        Assert.Equal("revision_unavailable", failed.Error?.Code);
        Assert.False(failed.Error?.Retryable is true);
        Assert.Empty(agent.ReservedEndpoints);
        Assert.Empty(agent.ExecutedEndpoints);
        Assert.Equal(0, await DbContext.FunctionMeterEvents.CountDocumentsAsync(
            candidate => candidate.InvocationId == invocation.Id));
    }

    [Fact]
    public async Task Ambiguous_execute_is_reconciled_only_on_the_same_agent_and_never_rerouted()
    {
        var subject = CreateSubject();
        var invocation = CreateInvocation(subject);
        await InsertAsync(subject, invocation);
        var agent = new RecordingAgentClient
        {
            ExecuteHandler = (_, _, _) => throw new HttpRequestException("Connection closed after upload."),
            ObserveHandler = (_, _) => throw new HttpRequestException("Agent remains unreachable.")
        };

        await CreateDispatcher(agent, [FirstEndpoint, SecondEndpoint]).RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { FirstEndpoint }, agent.ReservedEndpoints);
        Assert.Equal(new[] { FirstEndpoint }, agent.ExecutedEndpoints);
        Assert.Equal(new[] { FirstEndpoint }, agent.ObservedEndpoints);
        var persisted = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == invocation.Id)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.Dispatching, persisted.State);
        Assert.NotNull(persisted.Lease);
        Assert.Equal(0, await DbContext.FunctionMeterEvents.CountDocumentsAsync(
            candidate => candidate.InvocationId == invocation.Id));
    }

    private FunctionInvocationDispatcher CreateDispatcher(
        RecordingAgentClient agent,
        IReadOnlyList<FunctionEndpoint>? endpoints = null)
    {
        var snapshot = new FunctionEndpointSnapshot(
            new FunctionServiceKey("pole-tenant-rg", "function-cleanup-agent"),
            (endpoints ?? [FirstEndpoint]).ToImmutableArray(),
            Now);
        return new FunctionInvocationDispatcher(
            DbContext,
            new StubEndpointRegistry(snapshot),
            new SnapshotOrderEndpointSelector(),
            new UnexpectedActivator(),
            agent,
            new FunctionTimerOptions
            {
                BatchSize = 10,
                DispatchLeaseSeconds = 30,
                ExecutionTimeoutSeconds = 10
            },
            new FixedTimeProvider(Now),
            NullLogger<FunctionInvocationDispatcher>.Instance);
    }

    private async Task InsertAsync(FunctionSubject subject, FunctionInvocation invocation)
    {
        await DbContext.FunctionApps.InsertOneAsync(subject.App);
        await DbContext.FunctionRevisions.InsertOneAsync(subject.Revision);
        await DbContext.FunctionInvocations.InsertOneAsync(invocation);
    }

    private static FunctionInvocation CreateInvocation(
        FunctionSubject subject,
        FunctionRetryPolicy? retry = null,
        string? tenantId = null) =>
        FunctionInvocation.CreateTimer(
            tenantId ?? subject.App.TenantId,
            subject.App.Id,
            subject.Revision.Id,
            subject.Trigger.TriggerId,
            "cleanup",
            subject.Trigger.ScheduleVersion,
            Now.AddMinutes(-1),
            retry ?? subject.Trigger.Retry,
            Now.AddMinutes(-1),
            isPastDue: true,
            missedOccurrenceCount: 1);

    private static FunctionSubject CreateSubject()
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
            new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            [
                new FunctionHandlerSpec(
                    "cleanup",
                    1,
                    new TimerFunctionTriggerSpec(
                        string.Empty,
                        "* * * * *",
                        "UTC",
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        ScheduleVersion: 1,
                        Retry: new FunctionRetryPolicy(3, [5, 30])))
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            "pole-tenant-rg",
            "cleanup",
            "cleanup",
            spec,
            [],
            Now.AddMinutes(-5));
        const string appId = "507f1f77bcf86cd799439021";
        const string revisionId = "507f1f77bcf86cd799439022";
        var app = created.FunctionApp with
        {
            Id = appId,
            ActiveRevisionId = revisionId,
            PendingRevisionId = null,
            Status = FunctionAppStatus.Ready
        };
        var revision = (created.Revision with
        {
            Id = revisionId,
            FunctionAppId = appId
        }).Activate(Now.AddMinutes(-4));
        var trigger = Assert.IsType<TimerFunctionTriggerSpec>(revision.Spec.Handlers.Single().Trigger);
        return new FunctionSubject(app, revision, trigger);
    }

    private static FunctionAgentProtocolResponse<FunctionAttemptStatusResponse> SuccessfulCompletion(
        FunctionEndpoint endpoint,
        string attemptId) =>
        new(
            HttpStatusCode.OK,
            new FunctionAttemptStatusResponse(
                endpoint.TargetUid!,
                Now.AddSeconds(2),
                new CompletedFunctionAttemptStatus(
                    attemptId,
                    Now,
                    Now.AddSeconds(2),
                    FunctionAttemptOutcome.Succeeded,
                    new TimerFunctionInvocationResult(),
                    Failure: null)),
            Error: null);

    private static string MeterId(
        string invocationId,
        int attemptNumber,
        FunctionMeterEventPhase phase)
    {
        var value = string.Join('\n', "meter", invocationId, attemptNumber.ToString(), phase.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record FunctionSubject(
        FunctionApp App,
        FunctionRevision Revision,
        TimerFunctionTriggerSpec Trigger);

    private sealed class StubEndpointRegistry(FunctionEndpointSnapshot snapshot) : IFunctionEndpointRegistry
    {
        public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) => snapshot;
        public void ReplaceAll(IEnumerable<V1EndpointSlice> slices) => throw new NotSupportedException();
        public void ApplySlice(V1EndpointSlice slice) => throw new NotSupportedException();
        public void DeleteSlice(string @namespace, string sliceName) => throw new NotSupportedException();
    }

    private sealed class SnapshotOrderEndpointSelector : IFunctionEndpointSelector
    {
        public IReadOnlyList<FunctionEndpoint> Order(FunctionEndpointSnapshot snapshot) => snapshot.Endpoints;
    }

    private sealed class UnexpectedActivator : IFunctionRevisionActivator
    {
        public Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A ready endpoint snapshot should not activate the revision.");
    }

    private sealed class RecordingAgentClient : IFunctionAgentClient
    {
        public List<FunctionEndpoint> ReservedEndpoints { get; } = [];
        public List<FunctionEndpoint> ExecutedEndpoints { get; } = [];
        public List<FunctionEndpoint> ObservedEndpoints { get; } = [];
        public List<ReserveFunctionAttemptRequest> ReserveRequests { get; } = [];
        public List<ExecuteFunctionAttemptRequest> ExecuteRequests { get; } = [];

        public Func<FunctionEndpoint, ReserveFunctionAttemptRequest,
            Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>>>? ReserveHandler { get; init; }
        public Func<FunctionEndpoint, string, ExecuteFunctionAttemptRequest,
            Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>>>? ExecuteHandler { get; init; }
        public Func<FunctionEndpoint, string,
            Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>>>? ObserveHandler { get; init; }

        public Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            ReserveFunctionAttemptRequest request,
            CancellationToken cancellationToken)
        {
            ReservedEndpoints.Add(endpoint);
            ReserveRequests.Add(request);
            return ReserveHandler?.Invoke(endpoint, request) ?? Task.FromResult(new FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>(
                HttpStatusCode.OK,
                new ReserveFunctionAttemptResponse(
                    request.Attempt.AttemptId,
                    "reservation-token",
                    Now,
                    Now.AddSeconds(5),
                    IsReplay: false),
                Error: null));
        }

        public Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            ExecuteFunctionAttemptRequest request,
            CancellationToken cancellationToken)
        {
            ExecutedEndpoints.Add(endpoint);
            ExecuteRequests.Add(request);
            return ExecuteHandler?.Invoke(endpoint, attemptId, request) ?? Task.FromResult(new FunctionAgentProtocolResponse<StartFunctionAttemptResponse>(
                HttpStatusCode.Accepted,
                new StartFunctionAttemptResponse(
                    attemptId,
                    Now,
                    IsReplay: false,
                    StatusUrl: PoleFunctionProtocol.AttemptStatusPath(attemptId)),
                Error: null));
        }

        public Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            int waitUpToMilliseconds,
            CancellationToken cancellationToken)
        {
            ObservedEndpoints.Add(endpoint);
            return ObserveHandler?.Invoke(endpoint, attemptId) ??
                   Task.FromResult(SuccessfulCompletion(endpoint, attemptId));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
