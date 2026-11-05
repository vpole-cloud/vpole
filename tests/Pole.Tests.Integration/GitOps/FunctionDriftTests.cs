using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Migrations;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private IMongoCollection<FunctionApp> FunctionApps => Fixture.Database.GetCollection<FunctionApp>("FunctionApps");
    private IMongoCollection<FunctionRevision> FunctionRevisions =>
        Fixture.Database.GetCollection<FunctionRevision>("FunctionRevisions");
    private IMongoCollection<FunctionRouteBinding> FunctionRoutes =>
        Fixture.Database.GetCollection<FunctionRouteBinding>("FunctionRouteBindings");

    private async Task SeedFunctionPlanningCatalog()
    {
        await new FunctionAppsInitialMigration().Up(Fixture.Database);
        var computes = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var compute = await computes.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        compute = compute with { AllowedResourceTypes = compute.AllowedResourceTypes.Add("pole/function-app") };
        await computes.ReplaceOneAsync(item => item.Id == compute.Id, compute);
        await Fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions").ReplaceOneAsync(
            item => item.Id == $"{compute.Id}/1", ComputeProfileRevisionFactory.Create(compute),
            new ReplaceOptions { IsUpsert = true });
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        await targets.ReplaceOneAsync(item => item.Id == target.Id, target with
        {
            WorkloadConfiguration = target.WorkloadConfiguration with
            {
                NetworkSources = new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty
                    .Add(FunctionResourceDeployment.GatewaySourceName,
                        DeploymentTargetCidrNetworkSource.Create("192.0.2.9/32")))
            }
        });
    }

    private static ManifestFile[] FunctionSnapshot(bool secrets = false, string route = "function-worker",
        string? scope = "function-scope") =>
    [
        new("group.yaml", """
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata: {name: function-group}
            spec: {description: Functions}
            ---
            apiVersion: pole.io/v1alpha1
            kind: IsolationScope
            metadata: {name: function-scope}
            spec: {resourceGroup: function-group}
            """),
        new("function.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: FunctionApp
            metadata: {name: function-worker}
            spec:
              resourceGroup: function-group
              functionRoute: {{route}}
              placement:
                deploymentTarget: lab
                computeProfile: {name: small, revision: 1}
                {{(scope is null ? "" : $"isolationScope: {scope}")}}
              image: {server: example.test, name: function, tag: one}
              environment: {APP_MODE: production}
              {{(secrets ? "secretEnvironment: {STAGE_FIVE_SECRET: {secret: function-secret, key: password}}" : "")}}
              resources:
                requests: {cpuMillis: 100, memoryMi: 256}
                limits: {cpuMillis: 500, memoryMi: 512}
              scaling:
                minInstances: 0
                maxInstances: 2
                targetConcurrencyPerInstance: 1
                maxConcurrencyPerInstance: 10
                scaleDownDelaySeconds: 60
                activationTimeoutSeconds: 120
              handlers:
                - name: hello
                  maxConcurrencyPerInstance: 10
                  trigger: {route: hello, methods: [GET], exposure: Internal, type: http}
                - name: clock
                  maxConcurrencyPerInstance: 1
                  trigger:
                    type: timer
                    cron: '*/5 * * * *'
                    concurrencyPolicy: Forbid
                    misfirePolicy: Skip
                    retry: {maxAttempts: 2, backoffSeconds: [5]}
              tags: [functions]
            """),
        .. secrets ? new[] { new ManifestFile("secret.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: Secret
            metadata: {name: function-secret}
            spec:
              resourceGroup: function-group
              {{(scope is null ? "" : $"isolationScope: {scope}")}}
              keys: [password]
            ---
            apiVersion: pole.io/v1alpha1
            kind: SecretBinding
            metadata: {name: function-access}
            spec:
              secret: function-secret
              consumer: {kind: FunctionApp, name: function-worker}
            """) } : []
    ];

    private async Task<FunctionApp> CurrentFunction() =>
        await FunctionApps.Find(Builders<FunctionApp>.Filter.Empty).SingleAsync();

    private async Task FailFunctionRuntime(FunctionApp app)
    {
        var failed = await Services.GetRequiredService<FailFunctionRevision>()(app.TenantId, app.Id,
            app.PendingRevisionId!, new("TestRuntimeFailure", "Runtime failure"));
        Assert.IsType<FunctionRevisionLifecycleCompleted>(failed.Value);
    }

    [Fact]
    public async Task FunctionSameShaRepairsManagedFieldsThenNoOpsWithoutNewRevisionOrTriggerIdentity()
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot();
        var initial = await Apply(files);
        Assert.True(initial.Snapshot.Valid, JsonSerializer.Serialize(initial.Snapshot.Diagnostics));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, initial.Status);
        var original = await CurrentFunction();
        var first = await FunctionRevisions.Find(item => item.Id == original.PendingRevisionId).SingleAsync();
        Assert.Equal(2, original.NextRevisionNumber);
        Assert.All(first.Spec.Handlers, handler => Assert.NotEmpty(handler.Trigger.TriggerId));
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == original.Id).SingleAsync();
        var scope = await DbContext.IsolationScopes.Find(item => item.Name == "function-scope").SingleAsync();
        Assert.Equal(scope.Id, placement.IsolationScopeId);
        Assert.Equal(first.Spec.Resources.Requests.CpuMillis, placement.ResolvedPlacement.Compute.Requests.CpuMillis);
        await FunctionRevisions.UpdateOneAsync(item => item.Id == first.Id,
            Builders<FunctionRevision>.Update.Set(item => item.Spec.Image, first.Spec.Image with
            { Digest = "sha256:server-owned", PullSecretName = "server-owned-credential" }));
        await FailFunctionRuntime(original);
        var failed = await CurrentFunction();
        Assert.Null(failed.ActiveRevisionId);
        Assert.Null(failed.PendingRevisionId);
        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(2, (await CurrentFunction()).NextRevisionNumber);

        await FunctionApps.UpdateOneAsync(item => item.Id == original.Id,
            Builders<FunctionApp>.Update.Set(item => item.Tags, ImmutableHashSet.Create("private-drift"))
                .Inc(item => item.PlanningFence, 1));
        await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id,
            Builders<ResourceDeployment>.Update.Set(item => item.ResolvedPlacement.Compute.Limits,
                placement.ResolvedPlacement.Compute.Limits with { CpuMillis = 9999 }).Inc(item => item.Revision, 1));
        var downloads = repository.ReadCommits.Count;
        var repaired = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, repaired.Status);
        Assert.Equal(downloads, repository.ReadCommits.Count);
        var current = await CurrentFunction();
        Assert.Equal(3, current.NextRevisionNumber);
        Assert.True(original.Tags.SetEquals(current.Tags));
        var second = await FunctionRevisions.Find(item => item.Id == current.PendingRevisionId).SingleAsync();
        Assert.Equal("sha256:server-owned", second.Spec.Image.Digest);
        Assert.Equal("server-owned-credential", second.Spec.Image.PullSecretName);
        Assert.Equal(first.Spec.Handlers.Select(handler => handler.Trigger.TriggerId),
            second.Spec.Handlers.Select(handler => handler.Trigger.TriggerId));
        Assert.Equal(1, Assert.IsType<TimerFunctionTriggerSpec>(second.Spec.Handlers
            .Single(handler => handler.Name == "clock").Trigger).ScheduleVersion);
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(TestData.TenantId,
            source, repaired.Context!.RunId, 1, 20, default))!.Items;
        Assert.DoesNotContain("private-drift", JsonSerializer.Serialize(operations));
        var configure = operations.Single(item => item.Kind == "FunctionApp" &&
            item.Phase == DeclarationPhase.Configure).Plan!;
        Assert.Equal(GitPlannedAction.Update, configure.Action);
        Assert.Contains("configuration.tags", configure.ChangedFields);
        events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var stable = await CurrentFunction();
        Assert.Equal(current.NextRevisionNumber, stable.NextRevisionNumber);
        Assert.Equal(current.UpdatedAtUtc, stable.UpdatedAtUtc);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(2, await FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty));
        var detail = await ActivatorUtilities.CreateInstance<GetFunctionAppByIdHandler>(Services)
            .HandleAsync(new(original.Id, original.TenantId));
        Assert.NotNull(Assert.IsType<FunctionAppDetailsDto>(detail.Value).FunctionApp.GitOps);
        var manualSpec = new FunctionRevisionRequest(new("example.test", "function", "manual"),
            second.Spec.Configuration, second.Spec.Resources, second.Spec.Scaling,
            [new("hello", 10, new HttpFunctionTriggerRequest("hello", ["GET"], FunctionExposure.Internal))]);
        var updated = await ActivatorUtilities.CreateInstance<UpdateFunctionAppHandler>(Services)
            .HandleAsync(new(original.Id, new(manualSpec, ["manual"]), original.TenantId));
        Assert.IsType<FunctionAppGitOwnedConfiguration>(Assert.IsType<UpdateFunctionAppError>(updated.Value).Value);
        var deleted = await ActivatorUtilities.CreateInstance<DeleteFunctionAppHandler>(Services)
            .HandleAsync(new(original.Id, original.TenantId));
        Assert.IsType<FunctionAppGitOwnedConfiguration>(Assert.IsType<DeleteFunctionAppError>(deleted.Value).Value);
    }

    [Fact]
    public async Task FunctionMissingSecretKeepsPreparedInertThenResumesAndRotationIsNoOp()
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot(secrets: true);
        var blocked = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Operations, item => item.Failure is FunctionDeclarationSecretRejected
        { Reason: Pole.Features.Secrets.Contracts.ConsumerSecretReferenceError.SecretValuesMissing });
        var prepared = await CurrentFunction();
        Assert.Equal(FunctionAppDesiredState.Prepared, prepared.DesiredState);
        Assert.Equal(1, prepared.NextRevisionNumber);
        Assert.Equal(0, await FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty));
        Assert.Empty((await FunctionRoutes.Find(item => item.FunctionAppId == prepared.Id).SingleAsync()).HttpRoutes);
        Assert.IsType<PreparedResourceDeploymentState>((await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == prepared.Id).SingleAsync()).State);
        await Supply("not-exposed-to-gitops");
        var resumed = await Pipeline.ResumeAsync(blocked.Context!);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, resumed.Status);
        var app = await CurrentFunction();
        Assert.Equal(prepared.Id, app.Id);
        Assert.Equal(FunctionAppDesiredState.Enabled, app.DesiredState);
        Assert.Equal(2, app.NextRevisionNumber);
        var secret = await Secrets.Find(Builders<Pole.Features.Secrets.Domain.Secret>.Filter.Empty).SingleAsync();
        var revision = await FunctionRevisions.Find(item => item.Id == app.PendingRevisionId).SingleAsync();
        Assert.Equal(secret.Id, revision.Spec.Configuration.SecretReferences["STAGE_FIVE_SECRET"].SecretId);
        await Supply("rotated-private-value");
        var events = await BusinessEvents();
        var stable = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, stable.Status);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(2, (await CurrentFunction()).NextRevisionNumber);
        var view = await Services.GetRequiredService<GitRepositoryQueries>().RunAsync(TestData.TenantId, source,
            stable.Context!.RunId, default);
        Assert.DoesNotContain("rotated-private-value", JsonSerializer.Serialize(view));
    }

    [Theory]
    [InlineData("route")]
    [InlineData("scope")]
    public async Task FunctionImmutableIdentityChangesBlockWithoutNewRevisions(string field)
    {
        await SeedFunctionPlanningCatalog();
        await Apply(FunctionSnapshot());
        var original = await CurrentFunction();
        var events = await BusinessEvents();
        var changed = field == "route" ? FunctionSnapshot(route: "other-route") : FunctionSnapshot(scope: null);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(changed)).Status);
        Assert.Equal(original.NextRevisionNumber, (await CurrentFunction()).NextRevisionNumber);
        Assert.Equal(events, await BusinessEvents());
    }

    [Fact]
    public async Task FunctionMissingCreatedIdentityAndMissingLatestRevisionAreNeverRecreated()
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot();
        await Apply(files);
        var app = await CurrentFunction();
        await FunctionRevisions.DeleteOneAsync(item => item.Id == app.PendingRevisionId);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(0, await FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty));
        await FunctionApps.DeleteOneAsync(item => item.Id == app.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(0, await FunctionApps.CountDocumentsAsync(Builders<FunctionApp>.Filter.Empty));
    }

    [Fact]
    public async Task FunctionNoOpReplansWhenManagedConfigurationChangesAfterPlanning()
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot();
        await Apply(files);
        await FailFunctionRuntime(await CurrentFunction());
        var intercepted = false;
        var mutations = new BeforeNoOp(Services.GetRequiredService<IGitOpsMutations>(), async (operation, ct) =>
        {
            if (intercepted || operation.Declaration.Address.Kind != "FunctionApp" ||
                operation.Phase != DeclarationPhase.Configure)
                return;

            intercepted = true;
            var app = await CurrentFunction();
            await FunctionApps.UpdateOneAsync(item => item.Id == app.Id,
                Builders<FunctionApp>.Update.Set(item => item.Tags, ImmutableHashSet.Create("racing-change")),
                cancellationToken: ct);
        });
        var result = await new RepositoryPipelineDriver(Services, Fixture, repository, customMutations: mutations)
            .SubmitAsync(new(TestData.TenantId, source), files);
        Assert.True(intercepted);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.True((await CurrentFunction()).Tags.SetEquals(["functions"]));
        Assert.Equal(3, (await CurrentFunction()).NextRevisionNumber);
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("trigger-identity")]
    [InlineData("invalid-resources")]
    [InlineData("reserved-route")]
    [InlineData("reserved-environment")]
    [InlineData("missing-exposure")]
    public async Task FunctionInvalidSchemaRejectsWholeSnapshotBeforeMutations(string invalid)
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot();
        var content = files[1].Content;
        content = invalid switch
        {
            "unknown-root" => content.Replace("functionRoute: function-worker",
                "unknown: true\n  functionRoute: function-worker"),
            "trigger-identity" => content.Replace("exposure: Internal, type: http",
                "exposure: Internal, triggerId: client-selected, type: http"),
            "invalid-resources" => content.Replace("cpuMillis: 500", "cpuMillis: 0"),
            "reserved-route" => content.Replace("functionRoute: function-worker", "functionRoute: api"),
            "reserved-environment" => content.Replace("APP_MODE: production", "PoleFunctions__TenantId: unsafe"),
            "missing-exposure" => content.Replace("exposure: Internal, ", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        files[1] = files[1] with { Content = content };
        var result = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, result.Status);
        Assert.Equal(0, await FunctionApps.CountDocumentsAsync(Builders<FunctionApp>.Filter.Empty));
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Empty));
    }

    [Fact]
    public async Task FunctionConfigureRollsBackRevisionPlacementOwnershipAndEventsThenRecovers()
    {
        await SeedFunctionPlanningCatalog();
        var called = false;
        var original = Services.GetRequiredService<RequestResourceDeploymentSetApply>();
        RequestResourceDeploymentSetApply rejectAfterApply = async (request, session, ct) =>
        {
            var result = await original(request, session, ct);
            Assert.IsType<MutatedResourceDeployments>(result.Value);
            var app = await FunctionApps.Find(session, item => item.Id == request.ResourceId).SingleAsync(ct);
            Assert.NotNull(app.PendingRevisionId);
            Assert.Equal(1, await FunctionRevisions.CountDocumentsAsync(session,
                item => item.FunctionAppId == app.Id, cancellationToken: ct));
            called = true;
            return new ResourceDeploymentMutationError(new ResourceDeploymentPlacementUnavailable(
                request.ResourceId, app.Placement.ResourceDeploymentId));
        };
        var providers = Services.GetServices<IDeclarativeResourceProvider>().ToArray();
        var provider = providers.Single(item => item.Kind == "FunctionApp");
        var executionType = provider.GetType().Assembly.GetType(
            "Pole.Features.Functions.Application.Declarations.FunctionDeclarationExecution", throwOnError: true)!;
        var execution = ActivatorUtilities.CreateInstance(Services, executionType, rejectAfterApply);
        var failing = (IDeclarativeResourceProvider)ActivatorUtilities.CreateInstance(Services,
            provider.GetType(), execution);
        var registry = new DeclarativeProviderRegistry(
            providers.Select(item => item.Kind == "FunctionApp" ? failing : item));
        var result = await new RepositoryPipelineDriver(Services, Fixture, repository, customProviders: registry)
            .SubmitAsync(new(TestData.TenantId, source), FunctionSnapshot());
        Assert.True(called);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        var prepared = await CurrentFunction();
        Assert.Equal(FunctionAppDesiredState.Prepared, prepared.DesiredState);
        Assert.Equal(1, prepared.NextRevisionNumber);
        Assert.Null(prepared.PendingRevisionId);
        Assert.Equal(0, await FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty));
        Assert.IsType<PreparedResourceDeploymentState>((await DbContext.ResourceDeployments
            .Find(item => item.ResourceId == prepared.Id).SingleAsync()).State);
        var inventory = await Inventory.Find(item => item.Kind == "FunctionApp").SingleAsync();
        Assert.Null(inventory.Projections.Applied);
        var events = await DbContext.OutboxMessages.Find(item => item.EventType.StartsWith("pole.functions/"))
            .ToListAsync();
        Assert.Empty(events);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.ResumeAsync(result.Context!)).Status);
        Assert.Equal(2, (await CurrentFunction()).NextRevisionNumber);
        Assert.Equal(1, await FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty));
    }
}
