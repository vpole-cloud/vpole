using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionAppDomainTests
{
    [Fact]
    public void Prepared_identity_has_no_executable_revision_and_round_trips_current_schema()
    {
        var options = FunctionAppFactory.DefaultCreateOptions;
        var app = FunctionAppFactory.CreatePrepared(
            "507f1f77bcf86cd799439011", "507f1f77bcf86cd799439012",
            new("507f1f77bcf86cd799439013", "507f1f77bcf86cd799439014", "507f1f77bcf86cd799439015"),
            "worker", "worker", DateTimeOffset.UtcNow, options);

        Assert.Equal(FunctionAppDesiredState.Prepared, app.DesiredState);
        Assert.Equal(FunctionAppStatus.Prepared, app.Status);
        Assert.Null(app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(1, app.NextRevisionNumber);
        Assert.Equal(0, app.PlanningFence);
        Assert.Equal(new FunctionQuotaAllocation(1, 0, 0, 0), app.AdmittedQuota);
        var roundTrip = BsonSerializer.Deserialize<FunctionApp>(app.ToBson());
        Assert.Equal(app.Id, roundTrip.Id);
        Assert.Equal(app.DesiredState, roundTrip.DesiredState);
        Assert.Equal(app.PlanningFence, roundTrip.PlanningFence);
        var route = FunctionRouteBindingFactory.Reserve(app, app.CreatedAtUtc);
        Assert.Equal(0, route.PlanningFence);
        Assert.Equal(FunctionRouteBindingState.Reserved, route.State);
        Assert.Empty(route.HttpRoutes);
        Assert.Null(route.ActiveRevisionId);
        Assert.Equal(0, BsonSerializer.Deserialize<FunctionRouteBinding>(route.ToBson()).PlanningFence);
    }

    [Fact]
    public void Logical_secret_references_round_trip_without_native_names()
    {
        var configuration = new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty
                .Add("TOKEN", new("507f1f77bcf86cd799439099", "source-token")));
        var bson = configuration.ToBson();
        var restored = BsonSerializer.Deserialize<FunctionConfiguration>(bson);
        Assert.Equal(configuration.SecretReferences["TOKEN"], restored.SecretReferences["TOKEN"]);
        var json = JsonSerializer.Serialize(configuration);
        var fromJson = JsonSerializer.Deserialize<FunctionConfiguration>(json)!;
        Assert.Equal(configuration.SecretReferences["TOKEN"], fromJson.SecretReferences["TOKEN"]);
        Assert.DoesNotContain("Kubernetes", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secret_reference_validation_requires_logical_id_and_key()
    {
        var validator = new FunctionConfigurationValidator();
        Assert.True(validator.Validate(new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty
                .Add("TOKEN", new("507f1f77bcf86cd799439099", "token")))).IsValid);
        Assert.False(validator.Validate(new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty
                .Add("TOKEN", new("native-kubernetes-secret", "token")))).IsValid);
    }

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
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
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
            .ToRevisionSpec();
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
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
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

        var readyAtUtc = DateTimeOffset.Parse("2026-07-17T10:00:10Z");
        var active = created.Revision.MarkReady(readyAtUtc).Activate();
        var document = active.ToBsonDocument();
        var statusDocument = document[nameof(FunctionRevision.Status)].AsBsonDocument;
        Assert.Equal("active", statusDocument["_t"].AsBsonArray[^1].AsString);
        var bson = document.ToBson();
        var restored = BsonSerializer.Deserialize<FunctionRevision>(bson);

        Assert.IsType<HttpFunctionTriggerSpec>(restored.Spec.Handlers[0].Trigger);
        Assert.IsType<TimerFunctionTriggerSpec>(restored.Spec.Handlers[1].Trigger);
        Assert.Equal(readyAtUtc, Assert.IsType<ActiveFunctionRevisionStatus>(restored.Status).ReadyAtUtc);
        Assert.Equal(0, created.Revision.PlanningFence);
        Assert.Equal(0, restored.PlanningFence);
    }

    [Fact]
    public void Revision_dto_serializes_only_its_nested_status_polymorphically()
    {
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
            "order-functions",
            "orders",
            CreateRevisionRequest(new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Public))
                .ToDomain(),
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));
        var active = created.Revision
            .MarkReady(DateTimeOffset.Parse("2026-07-17T10:00:10Z"))
            .Activate();

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            active.ToDto(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.False(json.RootElement.TryGetProperty("type", out _));
        Assert.Equal("active", json.RootElement.GetProperty("status").GetProperty("type").GetString());
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
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
            "order-functions",
            "orders",
            CreateRevisionRequest(
                    new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Public))
                .ToDomain(),
            [],
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"));

        Assert.IsType<ProvisioningFunctionRevisionStatus>(created.Revision.Status);
        var ready = created.Revision.MarkReady(DateTimeOffset.Parse("2026-07-17T10:00:10Z"));
        Assert.IsType<ReadyFunctionRevisionStatus>(ready.Status);
        var active = ready.Activate();
        Assert.IsType<ActiveFunctionRevisionStatus>(active.Status);
        var draining = active.Drain();
        Assert.IsType<DrainingFunctionRevisionStatus>(draining.Status);
        Assert.IsType<RetiredFunctionRevisionStatus>(draining.Retire().Status);
        var failed = created.Revision.Fail(new FunctionRevisionFailure("startup", "failed"));
        Assert.IsType<FailedFunctionRevisionStatus>(failed.Status);
        Assert.IsType<FailedFunctionRevisionStatus>(ready.Fail(new FunctionRevisionFailure("promotion", "failed")).Status);
        Assert.Throws<InvalidOperationException>(() => active.Activate());
    }

    private static FunctionRevisionRequest CreateRevisionRequest(params FunctionTriggerRequest[] triggers) =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", "v1"),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
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
