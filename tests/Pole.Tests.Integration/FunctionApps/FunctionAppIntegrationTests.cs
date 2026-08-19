using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionAppIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Promotion_requires_and_preserves_a_durable_ready_boundary()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();

        Assert.IsType<ConflictFailure>(await lifecycle.PromoteAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var readiness = await Task.WhenAll(
            lifecycle.MarkReadyAsync(TestData.TenantId, created.FunctionAppId, created.RevisionId),
            lifecycle.MarkReadyAsync(TestData.TenantId, created.FunctionAppId, created.RevisionId));
        Assert.All(readiness, result => Assert.IsAssignableFrom<ISuccess>(result));

        var ready = await DbContext.FunctionRevisions
            .Find(revision => revision.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<ReadyFunctionRevisionStatus>(ready.Status);

        var promotions = await Task.WhenAll(
            lifecycle.PromoteAsync(TestData.TenantId, created.FunctionAppId, created.RevisionId),
            lifecycle.PromoteAsync(TestData.TenantId, created.FunctionAppId, created.RevisionId));
        Assert.All(promotions, result => Assert.IsAssignableFrom<ISuccess>(result));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("function-revision-promoted"));
    }

    [Fact]
    public async Task Create_and_promote_atomically_publish_revision_and_routes()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();

        var promotion = await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId);

        Assert.IsAssignableFrom<ISuccess>(promotion);
        var app = await DbContext.FunctionApps.Find(candidate => candidate.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(created.RevisionId, app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Ready, app.Status);

        var revision = await DbContext.FunctionRevisions.Find(candidate => candidate.Id == created.RevisionId).SingleAsync();
        var activeStatus = Assert.IsType<ActiveFunctionRevisionStatus>(revision.Status);

        var route = await DbContext.FunctionRouteBindings
            .Find(candidate => candidate.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(FunctionRouteBindingState.Active, route.State);
        Assert.Equal(created.RevisionId, route.ActiveRevisionId);
        var httpRoute = Assert.Single(route.HttpRoutes);
        Assert.Equal("webhook", httpRoute.HandlerName);
        Assert.Equal(FunctionExposure.Public, httpRoute.Exposure);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("function-app-created"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("function-revision-promoted"));
        var timer = await DbContext.FunctionTimerStates
            .Find(candidate => candidate.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(created.RevisionId, timer.RevisionId);
        Assert.Equal(1, timer.ScheduleVersion);
        Assert.True(timer.Enabled);
        Assert.True(timer.NextOccurrenceUtc > activeStatus.ReadyAtUtc);
    }

    [Fact]
    public async Task Update_creates_immutable_revision_and_reconciles_trigger_identity()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));

        var original = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        var updatedSpec = RevisionRequest("*/10 * * * *", "v2") with
        {
            Resources = new FunctionResources(
                new FunctionResourceValues(200, 256),
                new FunctionResourceValues(500, 512))
        };
        var update = await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(updatedSpec, ["updated"]),
            TestData.TenantId));

        Assert.True(update.TryGetValue(out var response, out var error), error?.Message);
        Assert.Equal(2, response.RevisionNumber);
        var candidate = await DbContext.FunctionRevisions
            .Find(revision => revision.Id == response.RevisionId)
            .SingleAsync();
        Assert.IsType<ProvisioningFunctionRevisionStatus>(candidate.Status);
        Assert.Equal("v2", candidate.Spec.Image.Tag);
        Assert.Equal("v1", original.Spec.Image.Tag);

        var oldHttp = Assert.IsType<HttpFunctionTriggerSpec>(original.Spec.Handlers[0].Trigger);
        var newHttp = Assert.IsType<HttpFunctionTriggerSpec>(candidate.Spec.Handlers[0].Trigger);
        Assert.Equal(oldHttp.TriggerId, newHttp.TriggerId);
        var oldTimer = Assert.IsType<TimerFunctionTriggerSpec>(original.Spec.Handlers[1].Trigger);
        var newTimer = Assert.IsType<TimerFunctionTriggerSpec>(candidate.Spec.Handlers[1].Trigger);
        Assert.Equal(oldTimer.TriggerId, newTimer.TriggerId);
        Assert.Equal(oldTimer.ScheduleVersion + 1, newTimer.ScheduleVersion);

        var app = await DbContext.FunctionApps.Find(candidate => candidate.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(response.RevisionId, app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Updating, app.Status);
        Assert.Equal(1000, app.AdmittedQuota.TotalCpuMillis);
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync("function-revision-created"));

        var promotion = await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            response.RevisionId);
        Assert.IsAssignableFrom<ISuccess>(promotion);
        Assert.IsAssignableFrom<ISuccess>(await lifecycle.PromoteAsync(
            TestData.TenantId,
            created.FunctionAppId,
            response.RevisionId));
        var promoted = await DbContext.FunctionRevisions
            .Find(revision => revision.FunctionAppId == created.FunctionAppId)
            .ToListAsync();
        Assert.IsType<DrainingFunctionRevisionStatus>(
            promoted.Single(revision => revision.Id == created.RevisionId).Status);
        Assert.IsType<ActiveFunctionRevisionStatus>(
            promoted.Single(revision => revision.Id == response.RevisionId).Status);
        var activeTimer = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(response.RevisionId, activeTimer.RevisionId);
        Assert.Equal(newTimer.ScheduleVersion, activeTimer.ScheduleVersion);
        Assert.Equal("*/10 * * * *", activeTimer.Cron);
    }

    [Fact]
    public async Task Promotion_repins_unchanged_timer_without_resetting_next_occurrence()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var before = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();

        var update = await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/5 * * * *", "v2"), []),
            TestData.TenantId));
        Assert.True(update.TryGetValue(out var candidate, out var failure), failure?.Message);
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId));

        var after = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(candidate.RevisionId, after.RevisionId);
        Assert.Equal(before.ScheduleVersion, after.ScheduleVersion);
        Assert.Equal(before.NextOccurrenceUtc, after.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Retire_transitions_only_draining_revision_and_publishes_event_once()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));

        var update = await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/10 * * * *", "v2"), []),
            TestData.TenantId));
        Assert.True(update.TryGetValue(out var candidate, out var failure), failure?.Message);
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId));

        Assert.IsAssignableFrom<ISuccess>(await lifecycle.RetireAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        Assert.IsAssignableFrom<ISuccess>(await lifecycle.RetireAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));

        var retired = await DbContext.FunctionRevisions
            .Find(revision => revision.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<RetiredFunctionRevisionStatus>(retired.Status);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("function-revision-retired"));
    }

    [Fact]
    public async Task Retire_is_blocked_while_revision_has_durable_work()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var oldTimer = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();

        var update = await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/10 * * * *", "v2"), []),
            TestData.TenantId));
        Assert.True(update.TryGetValue(out var candidate, out var failure), failure?.Message);
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId));
        var pending = FunctionInvocation.CreateTimer(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId,
            oldTimer.TriggerId,
            oldTimer.HandlerName,
            oldTimer.ScheduleVersion,
            oldTimer.NextOccurrenceUtc,
            oldTimer.Retry,
            oldTimer.UpdatedAtUtc);
        await DbContext.FunctionInvocations.InsertOneAsync(pending);

        var result = await lifecycle.RetireAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId);

        Assert.IsType<ConflictFailure>(result);
        var revision = await DbContext.FunctionRevisions
            .Find(item => item.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<DrainingFunctionRevisionStatus>(revision.Status);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("function-revision-retired"));
    }

    [Fact]
    public async Task Runtime_route_resolution_uses_binding_owned_tenant_and_active_revision()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var resolver = new FunctionRuntimeRouteResolver(DbContext);

        var resolved = await resolver.ResolveAsync(
            "ORDERS",
            "orders/42",
            "post",
            FunctionExposure.Public);

        Assert.NotNull(resolved);
        Assert.Equal(TestData.TenantId, resolved.App.TenantId);
        Assert.Equal(created.RevisionId, resolved.Revision.Id);
        Assert.Equal("webhook", resolved.Route.HandlerName);
        Assert.Null(await resolver.ResolveAsync(
            "orders",
            "orders/42",
            "POST",
            FunctionExposure.Internal));
    }

    [Fact]
    public async Task Failed_update_keeps_active_revision_and_releases_candidate_capacity()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));

        var update = await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/5 * * * *") with
            {
                Scaling = new FunctionScalingPolicy(0, 9, 4, 8, 60, 30)
            }, []),
            TestData.TenantId));
        Assert.True(update.TryGetValue(out var candidate, out var updateFailure), updateFailure?.Message);

        Assert.IsAssignableFrom<ISuccess>(await lifecycle.MarkReadyAsync(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId));

        var failed = await lifecycle.FailAsync(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId,
            new FunctionRevisionFailure("image-pull", "Image could not be pulled."));

        Assert.IsAssignableFrom<ISuccess>(failed);
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(created.RevisionId, app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Ready, app.Status);
        Assert.Equal(5, app.AdmittedQuota.WorkloadInstances);
        var revision = await DbContext.FunctionRevisions.Find(value => value.Id == candidate.RevisionId).SingleAsync();
        var failedStatus = Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
        Assert.Equal("image-pull", failedStatus.Failure.Code);
        Assert.IsAssignableFrom<ISuccess>(await lifecycle.FailAsync(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId,
            new FunctionRevisionFailure("image-pull", "Image could not be pulled.")));
    }

    [Fact]
    public async Task Tenant_scoping_hides_apps_from_other_tenants()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);

        var get = await Mediator.Send(new GetFunctionAppByIdRequest(
            created.FunctionAppId,
            "507f1f77bcf86cd799439012"));
        var delete = await Mediator.Send(new DeleteFunctionAppRequest(
            created.FunctionAppId,
            "507f1f77bcf86cd799439012"));

        get.AssertFailure();
        delete.AssertFailure();
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(FunctionAppDesiredState.Enabled, app.DesiredState);
    }

    [Fact]
    public async Task Delete_disables_route_but_keeps_quota_until_runtime_cleanup()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        Assert.IsAssignableFrom<ISuccess>(await MarkReadyAndPromoteAsync(
            lifecycle,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId));
        var before = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();

        var result = await Mediator.Send(new DeleteFunctionAppRequest(created.FunctionAppId, TestData.TenantId));

        Assert.True(result.TryGetValue(out _, out var failure), failure?.Message);
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        var route = await DbContext.FunctionRouteBindings
            .Find(value => value.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(FunctionAppDesiredState.Deleting, app.DesiredState);
        Assert.Equal(FunctionRouteBindingState.Disabled, route.State);
        Assert.Equal(before.AdmittedQuota, app.AdmittedQuota);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("function-app-delete-requested"));
        var timer = await DbContext.FunctionTimerStates
            .Find(value => value.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.False(timer.Enabled);
        Assert.Null(timer.Lease);
    }

    private static async Task<Result> MarkReadyAndPromoteAsync(
        IFunctionRevisionLifecycleService lifecycle,
        string tenantId,
        string functionAppId,
        string revisionId)
    {
        var ready = await lifecycle.MarkReadyAsync(tenantId, functionAppId, revisionId);
        return ready is IFailure
            ? ready
            : await lifecycle.PromoteAsync(tenantId, functionAppId, revisionId);
    }

    private async Task<CreateFunctionAppResponse> CreateAsync(string resourceGroupId)
    {
        var result = await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "order-functions",
                "orders",
                resourceGroupId,
                RevisionRequest("*/5 * * * *"),
                []),
            TestData.TenantId));
        Assert.True(result.TryGetValue(out var response, out var failure), failure?.Message);
        return response;
    }

    private static FunctionRevisionRequest RevisionRequest(string cron, string imageTag = "v1") =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", imageTag),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerRequest(
                    "webhook",
                    4,
                    new HttpFunctionTriggerRequest("orders/{id}", ["POST"], FunctionExposure.Public)),
                new FunctionHandlerRequest(
                    "cleanup",
                    1,
                    new TimerFunctionTriggerRequest(
                        cron,
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        new FunctionRetryPolicy(3, [5, 30])))
            ]);
}
