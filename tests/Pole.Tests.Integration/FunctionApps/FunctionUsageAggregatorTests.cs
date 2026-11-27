// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Runtime.Metering;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Metering;
using Pole.Features.Functions.Runtime.Timers;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Global)]
public sealed class FunctionUsageAggregatorTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:10:00Z");

    [Fact]
    public async Task Deterministic_meter_events_are_aggregated_exactly_once()
    {
        var invocation = FunctionInvocation.CreateTimer(
                "tenant-1",
                "app-1",
                "revision-1",
                "trigger-1",
                "cleanup",
                1,
                Now,
                new FunctionRetryPolicy(3, [5, 30]),
                Now)
            .AcquireDispatchLease("dispatcher", "lease", Now, Now.AddMinutes(5))
            .MarkRunning("lease", Now.AddSeconds(1));
        var started = FunctionMeterEvent.Started(invocation, Now.AddSeconds(1));
        var admitted = FunctionMeterEvent.Admitted(invocation, Now);
        var succeeded = invocation.Succeed("lease", Now.AddSeconds(3));
        var completed = FunctionMeterEvent.Completed(
            succeeded,
            FunctionMeterOutcome.Succeeded,
            executionDurationMilliseconds: 2_000,
            Now.AddSeconds(3));
        await DbContext.FunctionMeterEvents.InsertManyAsync([admitted, started, completed]);
        var options = new FunctionTimerOptions { BatchSize = 10 };
        var time = new FixedTimeProvider(Now.AddSeconds(5));
        LoadUnaggregatedFunctionMeterEvents loadUnaggregated
            = new MongoLoadUnaggregatedFunctionMeterEvents(DbContext.FunctionMeterEvents).InvokeAsync;
        AggregateFunctionMeterEvent aggregateMeter = new MongoAggregateFunctionMeterEvent(
            Fixture.Client,
            DbContext.FunctionMeterEvents,
            DbContext.FunctionUsageBuckets,
            time).InvokeAsync;

        await Task.WhenAll(
            new FunctionUsageAggregator(
                loadUnaggregated,
                aggregateMeter,
                options,
                NullLogger<FunctionUsageAggregator>.Instance).RunOnceAsync(CancellationToken.None),
            new FunctionUsageAggregator(
                loadUnaggregated,
                aggregateMeter,
                options,
                NullLogger<FunctionUsageAggregator>.Instance).RunOnceAsync(CancellationToken.None));

        var bucket = await DbContext.FunctionUsageBuckets
            .Find(FilterDefinition<FunctionUsageBucket>.Empty)
            .SingleAsync();
        Assert.Equal(1, bucket.InvocationCount);
        Assert.Equal(1, bucket.AttemptCount);
        Assert.Equal(1, bucket.SucceededAttemptCount);
        Assert.Equal(2_000, bucket.ExecutionDurationMilliseconds);
        Assert.Equal(
            3,
            await DbContext.FunctionMeterEvents.CountDocumentsAsync(meter => meter.AggregatedAtUtc != null));

        await new FunctionUsageAggregator(
            loadUnaggregated,
            aggregateMeter,
            options,
            NullLogger<FunctionUsageAggregator>.Instance).RunOnceAsync(CancellationToken.None);
        var unchanged = await DbContext.FunctionUsageBuckets
            .Find(FilterDefinition<FunctionUsageBucket>.Empty)
            .SingleAsync();
        Assert.Equal(bucket, unchanged);
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset now
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
