// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Timers;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Global4)]
public sealed class FunctionTimerSchedulerTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:10:30Z");

    [Fact]
    public async Task FireOnce_materializes_one_durable_catch_up_occurrence_and_advances_schedule()
    {
        var timer = CreateTimer(Now.AddMinutes(-10).AddSeconds(-30));
        await DbContext.FunctionTimerStates.InsertOneAsync(timer);
        var scheduler = CreateScheduler();

        await scheduler.RunOnceAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None);

        var invocation = await DbContext.FunctionInvocations
            .Find(FilterDefinition<FunctionInvocation>.Empty)
            .SingleAsync();
        Assert.Equal(FunctionInvocationState.Pending, invocation.State);
        Assert.True(invocation.IsPastDue);
        Assert.Equal(10, invocation.MissedOccurrenceCount);
        Assert.Equal(timer.RevisionId, invocation.RevisionId);
        var advanced = await DbContext.FunctionTimerStates.Find(candidate => candidate.Id == timer.Id).SingleAsync();
        Assert.True(advanced.NextOccurrenceUtc > Now);
        Assert.Null(advanced.Lease);
    }

    [Fact]
    public async Task Forbid_records_occurrence_as_skipped_when_previous_invocation_is_nonterminal()
    {
        var timer = CreateTimer(Now.AddSeconds(-30));
        var previous = FunctionInvocation.CreateTimer(
            timer.TenantId,
            timer.FunctionAppId,
            timer.RevisionId,
            timer.TriggerId,
            timer.HandlerName,
            timer.ScheduleVersion,
            Now.AddMinutes(-1),
            timer.Retry,
            Now.AddMinutes(-1));
        await DbContext.FunctionTimerStates.InsertOneAsync(timer);
        await DbContext.FunctionInvocations.InsertOneAsync(previous);

        await CreateScheduler().RunOnceAsync(CancellationToken.None);

        var invocations = await DbContext.FunctionInvocations
            .Find(candidate => candidate.TriggerId == timer.TriggerId)
            .ToListAsync();
        Assert.Equal(2, invocations.Count);
        Assert.Single(invocations, invocation => invocation.State == FunctionInvocationState.Pending);
        Assert.Single(invocations, invocation => invocation.State == FunctionInvocationState.Skipped);
        var meters = await DbContext.FunctionMeterEvents.Find(FilterDefinition<FunctionMeterEvent>.Empty).ToListAsync();
        Assert.Equal(2, meters.Count);
        var meter = Assert.Single(meters, candidate => candidate.Phase == FunctionMeterEventPhase.Completed);
        Assert.Equal(FunctionMeterOutcome.Skipped, meter.Outcome);
    }

    [Fact]
    public async Task Concurrent_schedulers_create_the_occurrence_once()
    {
        var timer = CreateTimer(Now.AddSeconds(-30));
        await DbContext.FunctionTimerStates.InsertOneAsync(timer);

        await Task.WhenAll(
            CreateScheduler().RunOnceAsync(CancellationToken.None),
            CreateScheduler().RunOnceAsync(CancellationToken.None));

        Assert.Equal(
            1,
            await DbContext.FunctionInvocations.CountDocumentsAsync(FilterDefinition<FunctionInvocation>.Empty));
        var advanced = await DbContext.FunctionTimerStates.Find(candidate => candidate.Id == timer.Id).SingleAsync();
        Assert.True(advanced.NextOccurrenceUtc > Now);
    }

    private FunctionTimerScheduler CreateScheduler()
    {
        var time = new FixedTimeProvider(Now);
        return new FunctionTimerScheduler(
            new MongoClaimNextFunctionTimer(DbContext.FunctionTimerStates, time).InvokeAsync,
            new MongoMaterializeFunctionTimerOccurrence(
                Fixture.Client,
                DbContext.FunctionTimerStates,
                DbContext.FunctionInvocations,
                DbContext.FunctionMeterEvents,
                time).InvokeAsync,
            new MongoReleaseFunctionTimerLease(DbContext.FunctionTimerStates, time).InvokeAsync,
            new FunctionTimerOptions { BatchSize = 10 },
            NullLogger<FunctionTimerScheduler>.Instance);
    }

    private static FunctionTimerState CreateTimer(DateTimeOffset nextOccurrenceUtc) => FunctionTimerState.Create(
        "tenant-1",
        "app-1",
        "revision-1",
        "trigger-1",
        "cleanup",
        "* * * * *",
        1,
        TimerConcurrencyPolicy.Forbid,
        TimerMisfirePolicy.FireOnce,
        new FunctionRetryPolicy(3, [5, 30]),
        nextOccurrenceUtc,
        nextOccurrenceUtc.AddMinutes(-1));

    private sealed class FixedTimeProvider(
        DateTimeOffset now
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
