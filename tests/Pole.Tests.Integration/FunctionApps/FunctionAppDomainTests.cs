using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionAppDomainTests
{
    [Fact]
    public void Create_owns_runtime_identity_and_reserves_route_without_active_entries()
    {
        var request = CreateRevisionRequest(
            new HttpFunctionTriggerRequest(
                "orders/{id}",
                ["post"],
                FunctionExposure.Public),
            new TimerFunctionTriggerRequest(
                "*/5 * * * *",
                TimerConcurrencyPolicy.Forbid,
                TimerMisfirePolicy.FireOnce,
                new FunctionRetryPolicy(3, [5, 30])));

        var result = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            "pole-tenant-group",
            "order-functions",
            "orders",
            request.ToDomain(),
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));

        Assert.Null(result.FunctionApp.ActiveRevisionId);
        Assert.Equal(result.Revision.Id, result.FunctionApp.PendingRevisionId);
        Assert.Equal(2, result.FunctionApp.NextRevisionNumber);
        Assert.Equal("1.0", result.Revision.Spec.RuntimeProtocolVersion);
        Assert.Null(result.Revision.Spec.Image.Digest);
        Assert.Null(result.Revision.Spec.Image.PullSecretName);
        Assert.All(result.Revision.Spec.Handlers, handler => Assert.False(string.IsNullOrWhiteSpace(handler.Trigger.TriggerId)));
        var timer = Assert.IsType<TimerFunctionTriggerSpec>(result.Revision.Spec.Handlers[1].Trigger);
        Assert.Equal(1, timer.ScheduleVersion);
        Assert.Empty(result.RouteBinding.HttpRoutes);
        Assert.Equal(FunctionRouteBindingState.Reserved, result.RouteBinding.State);
    }

    [Fact]
    public void Quota_profile_reserves_maximum_declared_capacity_even_at_scale_to_zero()
    {
        var spec = CreateRevisionRequest(
                new HttpFunctionTriggerRequest("hello", ["get"], FunctionExposure.Internal))
            .ToDomain()
            .ToRevisionSpec("namespace");
        var allocation = FunctionQuotaAllocation.FromRevision(spec);
        var profile = new FunctionAppQuotaProfile(new FunctionAppCountQuota());

        var delta = profile.Calculate(allocation);

        Assert.Equal(1, delta[FunctionAppCountQuota.QuotaKey]);
        Assert.Equal(500, delta[CpuQuota.QuotaKey]);
        Assert.Equal(640, delta[RamQuota.QuotaKey]);
        Assert.Equal(5, delta[WorkloadInstancesQuota.QuotaKey]);
    }

    [Fact]
    public void Polymorphic_trigger_specs_round_trip_through_bson()
    {
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            "namespace",
            "order-functions",
            "orders",
            CreateRevisionRequest(
                    new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Public),
                    new TimerFunctionTriggerRequest(
                        "*/5 * * * *",
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        new FunctionRetryPolicy(2, [5])))
                .ToDomain(),
            [],
            DateTimeOffset.UtcNow);

        var bson = created.Revision.ToBson();
        var restored = BsonSerializer.Deserialize<FunctionRevision>(bson);

        Assert.IsType<HttpFunctionTriggerSpec>(restored.Spec.Handlers[0].Trigger);
        Assert.IsType<TimerFunctionTriggerSpec>(restored.Spec.Handlers[1].Trigger);
    }

    [Theory]
    [InlineData("*/5 * * * *", true)]
    [InlineData("0,15,30,45 8-18 * * 1-5", true)]
    [InlineData("not-a-cron * * * *", false)]
    [InlineData("60 * * * *", false)]
    [InlineData("* 24 * * *", false)]
    [InlineData("* * * *", false)]
    public void Cron_validation_enforces_five_field_numeric_ranges(string expression, bool expected) =>
        Assert.Equal(expected, FunctionCronExpression.IsValid(expression));

    [Fact]
    public void Revision_transitions_preserve_lifecycle_invariants()
    {
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            "namespace",
            "order-functions",
            "orders",
            CreateRevisionRequest(
                    new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Public))
                .ToDomain(),
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));

        Assert.True(created.Revision.HasValidLifecycleState());
        var active = created.Revision.Activate(DateTimeOffset.Parse("2026-07-17T10:00:10Z"));
        Assert.True(active.HasValidLifecycleState());
        Assert.True(active.Drain().HasValidLifecycleState());
        var failed = created.Revision.Fail(new FunctionRevisionFailure("startup", "failed"));
        Assert.True(failed.HasValidLifecycleState());
        Assert.Throws<InvalidOperationException>(() => active.Activate(DateTimeOffset.UtcNow));
    }

    private static FunctionRevisionRequest CreateRevisionRequest(params FunctionTriggerRequest[] triggers) =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", "v1"),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            triggers.Select((trigger, index) => new FunctionHandlerRequest(
                    $"handler-{index}",
                    4,
                    trigger))
                .ToImmutableList());
}
