using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionInvocationApiTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task History_and_manual_retry_are_tenant_scoped()
    {
        var (appId, revisionId, timer) = await CreatePromotedAppAsync();
        var failed = FunctionInvocation.CreateTimer(
                TestData.TenantId,
                appId,
                revisionId,
                timer.TriggerId,
                "cleanup",
                timer.ScheduleVersion,
                timer.NextOccurrenceUtc,
                timer.Retry,
                timer.UpdatedAtUtc)
            .AcquireDispatchLease(
                "dispatcher",
                "lease",
                timer.UpdatedAtUtc,
                timer.UpdatedAtUtc.AddMinutes(5))
            .Fail(
                "lease",
                FunctionInvocationError.Create("failed", "Failed.", retryable: false),
                timer.UpdatedAtUtc.AddSeconds(1));
        await DbContext.FunctionInvocations.InsertOneAsync(failed);
        const string otherTenant = "507f1f77bcf86cd799439012";

        var hiddenHistory = await Mediator.Send(new GetFunctionInvocationsRequest(appId, 50, otherTenant));
        var hiddenRetry = await Mediator.Send(new RetryFunctionInvocationRequest(appId, failed.Id, otherTenant));
        Assert.IsType<Failure<IReadOnlyList<FunctionInvocationDto>>>(hiddenHistory);
        Assert.IsType<Failure<RetryFunctionInvocationResponse>>(hiddenRetry);

        var history = await Mediator.Send(new GetFunctionInvocationsRequest(appId, 50, TestData.TenantId));
        Assert.True(history.TryGetValue(out var items, out var historyFailure), historyFailure?.Message);
        Assert.Equal(failed.Id, Assert.Single(items).Id);
        var retried = await Mediator.Send(new RetryFunctionInvocationRequest(
            appId,
            failed.Id,
            TestData.TenantId));
        Assert.True(retried.TryGetValue(out var response, out var retryFailure), retryFailure?.Message);
        Assert.Equal(2, response.NextAttemptNumber);
        var stored = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == failed.Id).SingleAsync();
        Assert.Equal(FunctionInvocationState.RetryScheduled, stored.State);
        Assert.Equal("manual_retry", stored.Error?.Code);
    }

    [Fact]
    public async Task Manual_retry_respects_forbid_concurrency_policy()
    {
        var (appId, revisionId, timer) = await CreatePromotedAppAsync();
        var failed = FunctionInvocation.CreateTimer(
                TestData.TenantId,
                appId,
                revisionId,
                timer.TriggerId,
                timer.HandlerName,
                timer.ScheduleVersion,
                timer.NextOccurrenceUtc,
                timer.Retry,
                timer.UpdatedAtUtc)
            .AcquireDispatchLease(
                "dispatcher",
                "failed-lease",
                timer.UpdatedAtUtc,
                timer.UpdatedAtUtc.AddMinutes(5))
            .Fail(
                "failed-lease",
                FunctionInvocationError.Create("failed", "Failed.", retryable: false),
                timer.UpdatedAtUtc.AddSeconds(1));
        var active = FunctionInvocation.CreateTimer(
            TestData.TenantId,
            appId,
            revisionId,
            timer.TriggerId,
            timer.HandlerName,
            timer.ScheduleVersion,
            timer.NextOccurrenceUtc.AddMinutes(5),
            timer.Retry,
            timer.UpdatedAtUtc.AddSeconds(2));
        await DbContext.FunctionInvocations.InsertManyAsync([failed, active]);

        var result = await Mediator.Send(new RetryFunctionInvocationRequest(
            appId,
            failed.Id,
            TestData.TenantId));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
        var stored = await DbContext.FunctionInvocations.Find(candidate => candidate.Id == failed.Id).SingleAsync();
        Assert.Equal(FunctionInvocationState.Failed, stored.State);
    }

    [Fact]
    public async Task Usage_query_returns_only_owned_app_buckets_and_watermark()
    {
        var (appId, revisionId, timer) = await CreatePromotedAppAsync();
        var hour = FunctionUsageBucket.ToHour(timer.UpdatedAtUtc);
        var bucket = FunctionUsageBucket.Create(
            new FunctionMeterDimensions(
                TestData.TenantId,
                appId,
                revisionId,
                timer.TriggerId,
                timer.HandlerName,
                FunctionMeterTriggerKind.Timer),
            hour) with
        {
            InvocationCount = 3,
            AttemptCount = 4,
            UpdatedAtUtc = hour.AddMinutes(5)
        };
        await DbContext.FunctionUsageBuckets.InsertOneAsync(bucket);
        var invocation = FunctionInvocation.CreateTimer(
            TestData.TenantId,
            appId,
            revisionId,
            timer.TriggerId,
            timer.HandlerName,
            timer.ScheduleVersion,
            timer.NextOccurrenceUtc,
            timer.Retry,
            timer.UpdatedAtUtc);
        await DbContext.FunctionMeterEvents.InsertOneAsync(
            FunctionMeterEvent.Admitted(invocation, hour.AddMinutes(1)) with
            {
                AggregatedAtUtc = hour.AddMinutes(5)
            });

        var result = await Mediator.Send(new GetFunctionUsageRequest(
            appId,
            hour,
            hour.AddHours(1),
            TestData.TenantId));

        Assert.True(result.TryGetValue(out var usage, out var failure), failure?.Message);
        Assert.Equal(hour.AddMinutes(5), usage.AggregatedThroughUtc);
        var returned = Assert.Single(usage.Buckets);
        Assert.Equal(3, returned.InvocationCount);
        Assert.Equal(4, returned.AttemptCount);
    }

    private async Task<(string AppId, string RevisionId, FunctionTimerState Timer)> CreatePromotedAppAsync()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var create = await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "timer-functions",
                "timers",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId));
        Assert.True(create.TryGetValue(out var created, out var failure), failure?.Message);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await lifecycle.MarkReadyAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        Assert.IsAssignableFrom<ISuccess>(await lifecycle.PromoteAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var timer = await DbContext.FunctionTimerStates
            .Find(candidate => candidate.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        return (created.FunctionAppId, created.RevisionId, timer);
    }

    private static FunctionRevisionRequest RevisionRequest() =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", "v1"),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerRequest(
                    "cleanup",
                    1,
                    new TimerFunctionTriggerRequest(
                        "*/5 * * * *",
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        new FunctionRetryPolicy(3, [5, 30])))
            ]);
}
