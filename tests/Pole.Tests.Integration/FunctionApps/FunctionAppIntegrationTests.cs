using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Runtime.Gateway;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionAppIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Latest_accepted_configuration_survives_failed_runtime_and_cleared_pointers()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        (await Services.GetRequiredService<FailFunctionRevision>()(
            TestData.TenantId, created.FunctionAppId, created.RevisionId,
            new FunctionRevisionFailure("image-pull", "Image unavailable"))).RequireLifecycleSuccess();
        var app = await DbContext.FunctionApps.Find(item => item.Id == created.FunctionAppId).SingleAsync();
        Assert.Null(app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        var references = await Services.GetRequiredService<LoadFunctionAppRevisionReferences>()(app);
        Assert.Equal(created.RevisionId, references.LatestConfiguration?.Id);
        Assert.IsType<FailedFunctionRevisionStatus>(references.LatestConfiguration!.Status);

        await DbContext.FunctionRevisions.DeleteOneAsync(item => item.Id == created.RevisionId);
        Assert.Null((await Services.GetRequiredService<LoadFunctionAppRevisionReferences>()(app)).LatestConfiguration);
    }

    [Fact]
    public async Task Missing_logical_secret_rolls_back_create_and_all_revision_events()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var spec = RevisionRequest("*/5 * * * *") with
        {
            Configuration = new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty
                    .Add("TOKEN", new("507f1f77bcf86cd799439099", "token")))
        };
        var result = (await Mediator.Send(new CreateFunctionAppRequest(
            new("secret-functions", "secret-functions", group.Id, spec, []),
            TestData.TenantId))).HandlerOutcome();
        var error = Assert.IsAssignableFrom<CreateFunctionAppError>(result.Value);
        Assert.IsType<FunctionSecretReferenceRejected>(error.Value);
        Assert.Equal(0, await DbContext.FunctionApps.CountDocumentsAsync(FilterDefinition<FunctionApp>.Empty));
        Assert.Equal(0, await DbContext.FunctionRevisions.CountDocumentsAsync(
            FilterDefinition<FunctionRevision>.Empty));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(
            item => item.ResourceType == FunctionResourceDeployment.ResourceType.Value));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-app-created"));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-created"));
    }

    [Fact]
    public async Task Missing_logical_secret_rolls_back_update_without_replacing_active_revision()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        (await MarkReadyAndPromoteAsync(
            Services.GetRequiredService<MarkFunctionRevisionReady>(),
            Services.GetRequiredService<PromoteFunctionRevision>(),
            TestData.TenantId, created.FunctionAppId, created.RevisionId)).RequireLifecycleSuccess();
        var spec = RevisionRequest("*/10 * * * *", "v2") with
        {
            Configuration = new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty
                    .Add("TOKEN", new("507f1f77bcf86cd799439099", "token")))
        };
        var result = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId, new(spec, []), TestData.TenantId))).HandlerOutcome();
        var error = Assert.IsAssignableFrom<UpdateFunctionAppError>(result.Value);
        Assert.IsType<FunctionSecretReferenceRejected>(error.Value);
        var app = await DbContext.FunctionApps.Find(item => item.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(created.RevisionId, app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(1, await DbContext.FunctionRevisions.CountDocumentsAsync(
            item => item.FunctionAppId == app.Id));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-created"));
    }

    [Fact]
    public async Task Create_uses_explicit_named_scope_placement()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = Pole.Features.ResourceGroups.Domain.IsolationScopeFactory.CreateAdditional(
            TestData.TenantId, group.Id, "function-private",
            Pole.Features.ResourceGroups.Domain.IsolationScopeFactory.DefaultOptions);
        await Fixture.Database.GetCollection<Pole.Features.ResourceGroups.Domain.IsolationScope>("IsolationScopes")
            .InsertOneAsync(scope);
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new("scoped-functions", "scoped-functions", group.Id, RevisionRequest("*/5 * * * *"), [],
                Placement: new(TestData.DefaultDeploymentTargetId, scope.Id)),
            TestData.TenantId))).RequireSuccess();
        var placement = await DbContext.ResourceDeployments.Find(
            item => item.ResourceId == created.FunctionAppId).SingleAsync();
        Assert.Equal(scope.Id, placement.IsolationScopeId);
        Assert.Equal(TestData.DefaultDeploymentTargetId, placement.ResolvedPlacement.DeploymentTargetId);
    }

    [Fact]
    public async Task Create_maps_resource_group_not_found_from_registration_to_function_outcome()
    {
        var result = await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "missing-group-functions",
                "missing-group-functions",
                "507f1f77bcf86cd799439013",
                RevisionRequest("*/5 * * * *"),
                []),
            TestData.TenantId));

        Assert.IsType<ResourceRegistrationResourceGroupNotFound>(result.HandlerOutcome() switch
        {
            CreateFunctionAppError error => error switch
            {
                ResourceRegistrationMutationError registrationError => registrationError switch
                {
                    ResourceRegistrationResourceGroupNotFound notFound => notFound,
                    _ => throw new InvalidOperationException("Expected a Resource Group not-found registration outcome.")
                },
                _ => throw new InvalidOperationException("Expected a Resource Group not-found outcome.")
            },
            _ => throw new InvalidOperationException("Expected a Function App creation failure outcome.")
        });
    }

    [Fact]
    public async Task Promotion_requires_and_preserves_a_durable_ready_boundary()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        var retire = Services.GetRequiredService<RetireFunctionRevision>();

        Assert.IsType<FunctionRevisionNotReady>((await promote(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).LifecycleValue());
        Assert.IsType<FunctionRevisionNotDraining>((await retire(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).LifecycleValue());
        var readiness = await Task.WhenAll(
            markReady(TestData.TenantId, created.FunctionAppId, created.RevisionId),
            markReady(TestData.TenantId, created.FunctionAppId, created.RevisionId));
        Assert.All(readiness, result => result.RequireLifecycleSuccess());

        var ready = await DbContext.FunctionRevisions
            .Find(revision => revision.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<ReadyFunctionRevisionStatus>(ready.Status);

        var promotions = await Task.WhenAll(
            promote(TestData.TenantId, created.FunctionAppId, created.RevisionId),
            promote(TestData.TenantId, created.FunctionAppId, created.RevisionId));
        Assert.All(promotions, result => result.RequireLifecycleSuccess());
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-promoted"));
    }

    [Fact]
    public async Task Create_and_promote_atomically_publish_revision_and_routes()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();

        var promotion = await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId);

        promotion.RequireLifecycleSuccess();
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
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-app-created"));
        var registration = await DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(FunctionResourceDeployment.ResourceType.Value, registration.ResourceTypeId);
        Assert.Equal(group.Id, registration.ResourceGroupId);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-promoted"));
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
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();

        var original = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        var updatedSpec = RevisionRequest("*/10 * * * *", "v2") with
        {
            Resources = new FunctionResources(
                new FunctionResourceValues(200, 256),
                new FunctionResourceValues(500, 512))
        };
        var response = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(updatedSpec, ["updated"]),
            TestData.TenantId))).RequireSuccess();
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
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-created"));

        var promotion = await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            response.RevisionId);
        promotion.RequireLifecycleSuccess();
        (await promote(
            TestData.TenantId,
            created.FunctionAppId,
            response.RevisionId)).RequireLifecycleSuccess();
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
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var before = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();

        var candidate = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/5 * * * *", "v2"), []),
            TestData.TenantId))).RequireSuccess();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId)).RequireLifecycleSuccess();

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
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        var retire = Services.GetRequiredService<RetireFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();

        var candidate = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/10 * * * *", "v2"), []),
            TestData.TenantId))).RequireSuccess();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId)).RequireLifecycleSuccess();

        (await retire(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        (await retire(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();

        var retired = await DbContext.FunctionRevisions
            .Find(revision => revision.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<RetiredFunctionRevisionStatus>(retired.Status);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-retired"));
    }

    [Fact]
    public async Task Retire_is_blocked_while_revision_has_durable_work()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        var retire = Services.GetRequiredService<RetireFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var oldTimer = await DbContext.FunctionTimerStates
            .Find(timer => timer.FunctionAppId == created.FunctionAppId)
            .SingleAsync();

        var candidate = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/10 * * * *", "v2"), []),
            TestData.TenantId))).RequireSuccess();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId)).RequireLifecycleSuccess();
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

        var result = await retire(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId);

        Assert.IsType<FunctionRevisionHasDurableInvocations>(result.LifecycleValue());
        var revision = await DbContext.FunctionRevisions
            .Find(item => item.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<DrainingFunctionRevisionStatus>(revision.Status);
        Assert.Equal(0L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-revision-retired"));
    }

    [Fact]
    public async Task Runtime_route_resolution_uses_binding_owned_tenant_and_active_revision()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var resolver = new FunctionRuntimeRouteResolver(
            new MongoLoadFunctionRuntimeRouteTarget(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions,
                DbContext.FunctionRouteBindings).InvokeAsync);

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
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        var fail = Services.GetRequiredService<FailFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();

        var candidate = (await Mediator.Send(new UpdateFunctionAppRequest(
            created.FunctionAppId,
            new UpdateFunctionAppRequestBody(RevisionRequest("*/5 * * * *") with
            {
                Scaling = new FunctionScalingPolicy(0, 9, 4, 8, 60, 30)
            }, []),
            TestData.TenantId))).RequireSuccess();

        (await markReady(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId)).RequireLifecycleSuccess();

        var failed = await fail(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId,
            new FunctionRevisionFailure("image-pull", "Image could not be pulled."));

        failed.RequireLifecycleSuccess();
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(created.RevisionId, app.ActiveRevisionId);
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Ready, app.Status);
        Assert.Equal(5, app.AdmittedQuota.WorkloadInstances);
        var revision = await DbContext.FunctionRevisions.Find(value => value.Id == candidate.RevisionId).SingleAsync();
        var failedStatus = Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
        Assert.Equal("image-pull", failedStatus.Failure.Code);
        (await fail(
            TestData.TenantId,
            created.FunctionAppId,
            candidate.RevisionId,
            new FunctionRevisionFailure("image-pull", "Image could not be pulled."))).RequireLifecycleSuccess();
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

        Assert.IsType<FunctionAppNotFound>(get.HandlerOutcome() switch
        {
            FunctionAppNotFound notFound => notFound,
            _ => throw new InvalidOperationException("Expected a Function App not-found outcome.")
        });
        Assert.IsType<FunctionAppNotFound>(delete.HandlerOutcome() switch
        {
            DeleteFunctionAppError error => error switch
            {
                FunctionAppNotFound notFound => notFound,
                _ => throw new InvalidOperationException("Expected a Function App not-found outcome.")
            },
            _ => throw new InvalidOperationException("Expected a Function App deletion failure outcome.")
        });
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(FunctionAppDesiredState.Enabled, app.DesiredState);
    }

    [Fact]
    public async Task Delete_disables_route_but_keeps_quota_until_runtime_cleanup()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var promote = Services.GetRequiredService<PromoteFunctionRevision>();
        (await MarkReadyAndPromoteAsync(
            markReady,
            promote,
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var before = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();

        (await Mediator.Send(new DeleteFunctionAppRequest(
            created.FunctionAppId,
            TestData.TenantId))).RequireSuccess();
        var app = await DbContext.FunctionApps.Find(value => value.Id == created.FunctionAppId).SingleAsync();
        var route = await DbContext.FunctionRouteBindings
            .Find(value => value.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.Equal(FunctionAppDesiredState.Deleting, app.DesiredState);
        Assert.Equal(FunctionRouteBindingState.Disabled, route.State);
        Assert.Equal(before.AdmittedQuota, app.AdmittedQuota);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("pole.functions/v1/function-app-delete-requested"));
        var timer = await DbContext.FunctionTimerStates
            .Find(value => value.FunctionAppId == created.FunctionAppId)
            .SingleAsync();
        Assert.False(timer.Enabled);
        Assert.Null(timer.Lease);
    }

    [Fact]
    public async Task FinalizeDeletion_IsTenantScopedAndIdempotentlyPurgesOwnedState()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = await CreateAsync(group.Id);
        (await Mediator.Send(new DeleteFunctionAppRequest(
            created.FunctionAppId,
            TestData.TenantId))).RequireSuccess();
        var deployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == created.Placement.DeploymentId)
            .SingleAsync();
        var startedAtUtc = deployment.UpdatedAtUtc.AddSeconds(1);
        var claimId = ObjectId.GenerateNewId().ToString();
        var deleted = deployment
            .Start(
                deployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claimId,
                startedAtUtc,
                startedAtUtc.AddMinutes(1))
            .Complete(
                deployment.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claimId,
                [],
                startedAtUtc.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(
            item => item.Id == deployment.Id,
            deleted);
        var finalize = Services.GetRequiredService<FinalizeFunctionAppDeletion>();

        Assert.IsType<FunctionAppDeletionFinalized>((await finalize(
            "507f1f77bcf86cd799439099",
            created.FunctionAppId,
            deployment.Id)).Value);
        Assert.Equal(1L, await DbContext.FunctionApps.CountDocumentsAsync(
            item => item.Id == created.FunctionAppId));

        Assert.IsType<FunctionAppDeletionFinalized>((await finalize(
            TestData.TenantId,
            created.FunctionAppId,
            deployment.Id)).Value);
        Assert.IsType<FunctionAppDeletionFinalized>((await finalize(
            TestData.TenantId,
            created.FunctionAppId,
            deployment.Id)).Value);

        Assert.Equal(0L, await DbContext.FunctionApps.CountDocumentsAsync(
            item => item.Id == created.FunctionAppId));
        Assert.Equal(0L, await DbContext.FunctionRevisions.CountDocumentsAsync(
            item => item.FunctionAppId == created.FunctionAppId));
        Assert.Equal(0L, await DbContext.FunctionRouteBindings.CountDocumentsAsync(
            item => item.FunctionAppId == created.FunctionAppId));
        Assert.Equal(0L, await DbContext.FunctionTimerStates.CountDocumentsAsync(
            item => item.FunctionAppId == created.FunctionAppId));
        Assert.Equal(0L, await DbContext.ResourceDeployments.CountDocumentsAsync(
            item => item.Id == deployment.Id));
        Assert.Equal(0L, await DbContext.ResourceRegistrations.CountDocumentsAsync(
            registration => registration.ResourceId == created.FunctionAppId));
    }

    private static async Task<FunctionRevisionLifecycleOutcome> MarkReadyAndPromoteAsync(
        MarkFunctionRevisionReady markReady,
        PromoteFunctionRevision promote,
        string tenantId,
        string functionAppId,
        string revisionId)
    {
        var ready = await markReady(tenantId, functionAppId, revisionId);
        return ready is not FunctionRevisionLifecycleCompleted
            ? ready
            : await promote(tenantId, functionAppId, revisionId);
    }

    private async Task<CreateFunctionAppResponse> CreateAsync(string resourceGroupId)
    {
        var response = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "order-functions",
                "orders",
                resourceGroupId,
                RevisionRequest("*/5 * * * *"),
                []),
            TestData.TenantId))).RequireSuccess();
        return response;
    }

    private static FunctionRevisionRequest RevisionRequest(string cron, string imageTag = "v1") =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", imageTag),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
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
