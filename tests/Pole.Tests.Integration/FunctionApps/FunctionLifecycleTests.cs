using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Domain;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.GitOps;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.WebServices;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Global3)]
public sealed class FunctionLifecycleTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private LifecycleOwnerAuthority? _authority;
    private LifecycleOwnerAuthority authority => _authority ??= new LifecycleOwnerAuthority(Data.TenantId);
    private IMongoCollection<FunctionApp> Apps => Fixture.Database.GetCollection<FunctionApp>("FunctionApps");

    private IDeclarativeResourceLifecycleProvider Provider =>
        Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == "FunctionApp");

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IGitOpsMutations>(authority);
        services.AddSingleton<IGitLifecyclePlanGuard>(authority);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Fixture.PrepareNamedCatalogAsync();
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var compute = await profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        compute = compute with { AllowedResourceTypes = compute.AllowedResourceTypes.Add("pole/function-app") };
        await profiles.ReplaceOneAsync(item => item.Id == compute.Id, compute);
        await Fixture.Database
            .GetCollection<ComputeProfileRevision>("ComputeProfileRevisions")
            .ReplaceOneAsync(item => item.Id == $"{compute.Id}/1", ComputeProfileRevisionFactory.Create(compute));
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        await targets.ReplaceOneAsync(
            item => item.Id == target.Id,
            target with
            {
                WorkloadConfiguration = target.WorkloadConfiguration with
                {
                    NetworkSources = new DeploymentTargetNetworkSources(
                        ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add(
                            FunctionResourceDeployment.GatewaySourceName,
                            DeploymentTargetCidrNetworkSource.Create("192.0.2.9/32"))),
                },
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_deletion_preserves_git_owned_bindings_and_allows_unowned_bindings(bool gitOwned)
    {
        var current = await CreateApp();
        var group = await DbContext.ResourceGroups.Find(_ => true).SingleAsync();
        var secret = await ConsumerSecretDeletionTestData.InsertAsync(
            Fixture.Database,
            group,
            FunctionResourceDeployment.ResourceType,
            current.Id);
        if (gitOwned)
            authority.Managed.Add(("SecretBinding", secret.Bindings.Single().Id));
        var before = await State();
        var deleted = Assert.IsType<DeleteFunctionAppOutcome>(
            (await Mediator.Send(new DeleteFunctionAppRequest(current.Id, current.TenantId))).Value);
        if (gitOwned)
        {
            var error = Assert.IsType<DeleteFunctionAppError>(deleted.Value);
            var required = Assert.IsType<FunctionSecretBindingRemovalRequired>(error.Value);
            Assert.Equal(409, FunctionAppHttpMappings.MapSecretBindingRemovalRequired(required).StatusCode);
            Assert.Equal(before, await State());
            using var session = await Fixture.Client.StartSessionAsync();
            session.StartTransaction();
            var snapshot = await Snapshot(current, session);
            Assert.IsType<GitLifecycleDependencyRetained>(
                Assert.IsType<DeclarationReadBlocked>(
                        (await Provider.RemoveAsync(
                            new RemoveLifecycleResourceRequest(
                                authority.Authority,
                                snapshot.Resource,
                                snapshot.Version),
                            session,
                            default)).Value)
                    .Failure);
            await session.AbortTransactionAsync();
            Assert.Equal(before, await State());
        }
        else
        {
            Assert.IsType<DeleteFunctionAppResponse>(deleted.Value);
        }

        Assert.Single(
            (await Fixture.Database.GetCollection<Secret>("Secrets").Find(item => item.Id == secret.Id).SingleAsync())
            .Bindings);
    }

    [Fact]
    public async Task Removal_joins_quota_routes_timers_deployment_and_outbox_and_replay_waits_for_cleanup()
    {
        var current = await CreateApp();
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        Assert.IsType<FunctionRevisionLifecycleCompleted>(
            (await markReady(current.TenantId, current.Id, current.PendingRevisionId!)).Value);
        Assert.IsType<FunctionRevisionLifecycleCompleted>(
            (await Services.GetRequiredService<PromoteFunctionRevision>()(
                current.TenantId,
                current.Id,
                current.PendingRevisionId!)).Value);
        current = await Load(current.Id);
        Assert.True(
            (await DbContext.FunctionTimerStates.Find(item => item.FunctionAppId == current.Id).SingleAsync()).Enabled);
        var before = await State();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        Assert.IsType<GitLifecycleRemovalRequested>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, snapshot.Resource, snapshot.Version),
                session,
                default)).Value);
        Assert.Equal(GitLifecycleMutationEffect.Remove, authority.Effect);
        await session.AbortTransactionAsync();
        Assert.Equal(before, await State());

        session.StartTransaction();
        Assert.IsType<GitLifecycleRemovalRequested>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, snapshot.Resource, snapshot.Version),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(FunctionAppDesiredState.Deleting, (await Load(current.Id)).DesiredState);
        Assert.Equal(
            FunctionRouteBindingState.Disabled,
            (await DbContext.FunctionRouteBindings.Find(item => item.FunctionAppId == current.Id).SingleAsync()).State);
        var timer = await DbContext.FunctionTimerStates.Find(item => item.FunctionAppId == current.Id).SingleAsync();
        Assert.False(timer.Enabled);
        Assert.Null(timer.Lease);
        Assert.Equal(
            ResourceDeploymentOperation.Delete,
            (await DbContext.ResourceDeployments
                .Find(item => item.Id == current.Placement.ResourceDeploymentId)
                .SingleAsync()).DesiredOperation);
        var after = await State();
        session.StartTransaction();
        var pending = await Snapshot(current, session);
        Assert.Equal(GitLifecyclePresence.Deleting, pending.Presence);
        Assert.IsType<GitLifecycleRemovalRequested>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, pending.Resource, pending.Version),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(after, await State());
    }

    [Fact]
    public async Task Unreviewed_connection_blocks_the_entire_owner_mutation()
    {
        var current = await CreateApp();
        var connection = ResourceConnectionFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            current.TenantId,
            "unreviewed",
            FunctionResourceDeployment.ResourceType,
            current.Id,
            ResourceTypeId.Create("pole/redis"),
            ObjectId.GenerateNewId().ToString(),
            "redis",
            [
                ConnectionTargetSnapshot.Create(
                    "target",
                    "destination",
                    "cell",
                    "namespace",
                    "redis",
                    6379,
                    6379,
                    [new ConnectionSourcePlacement(current.Placement.ResourceDeploymentId, "cell", "namespace")]),
            ]);
        await Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections").InsertOneAsync(connection);
        var before = await State();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        var outcome = await Provider.RemoveAsync(
            new RemoveLifecycleResourceRequest(authority.Authority, snapshot.Resource, snapshot.Version),
            session,
            default);
        Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<DeclarationReadBlocked>(outcome.Value).Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(before, await State());
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("placement")]
    public async Task Changed_managed_fields_without_revision_invalidate_approval(string field)
    {
        var current = await CreateApp();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        await session.CommitTransactionAsync();
        if (field == "configuration")
        {
            var revision = await DbContext.FunctionRevisions
                .Find(item => item.FunctionAppId == current.Id)
                .SingleAsync();
            await DbContext.FunctionRevisions.ReplaceOneAsync(
                item => item.Id == revision.Id,
                revision with
                {
                    Spec = revision.Spec with
                    {
                        Configuration = revision.Spec.Configuration with
                        {
                            EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("CHANGED", "value"),
                        },
                    },
                });
        }
        else
        {
            var placement = await DbContext.ResourceDeployments
                .Find(item => item.ResourceId == current.Id)
                .SingleAsync();
            await DbContext.ResourceDeployments.UpdateOneAsync(
                item => item.Id == placement.Id,
                Builders<ResourceDeployment>.Update.Set(
                    item => item.ResolvedPlacement.Compute.Limits,
                    placement.ResolvedPlacement.Compute.Limits with { CpuMillis = 9999 }));
        }

        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, snapshot.Resource, snapshot.Version),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(FunctionAppDesiredState.Enabled, (await Load(current.Id)).DesiredState);
    }

    [Fact]
    public async Task Read_is_tenant_and_id_exact_and_never_rebinds_a_reused_name()
    {
        var current = await CreateApp();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(
                    ObjectId.GenerateNewId().ToString(),
                    new GitLifecycleResourceReference(new ResourceAddress("FunctionApp", current.Name), current.Id)),
                session,
                default)).Value);
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(
                    current.TenantId,
                    new GitLifecycleResourceReference(
                        new ResourceAddress("FunctionApp", current.Name),
                        ObjectId.GenerateNewId().ToString())),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Preview_preserves_artifact_credentials_trigger_ids_and_business_revision()
    {
        var current = await CreateApp();
        var revision = await DbContext.FunctionRevisions.Find(item => item.FunctionAppId == current.Id).SingleAsync();
        await DbContext.FunctionRevisions.ReplaceOneAsync(
            item => item.Id == revision.Id,
            revision with
            {
                Spec = revision.Spec with
                {
                    Image = revision.Spec.Image with
                    {
                        Digest = "sha256:server-owned", PullSecretName = "server-owned",
                    },
                },
            });
        var before = await State();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(Provider)
                .PreviewAdoptionAsync(
                    new PreviewLifecycleAdoptionRequest(
                        current.TenantId,
                        Declaration(current),
                        current.Id,
                        [
                            new GitPreviewResolvedReference(
                                new ResourceAddress("ResourceGroup", "alpha"),
                                current.ResourceGroupId),
                        ]),
                    session,
                    default)).Value);
        Assert.Equal(current.Id, preview.Current.ResourceId);
        Assert.True(JsonElement.DeepEquals(preview.Current.Projection.Identity, preview.Desired.Identity));
        Assert.True(JsonElement.DeepEquals(preview.Current.Projection.Configuration, preview.Desired.Configuration));
        Assert.DoesNotContain("server-owned", preview.Desired.Configuration.GetRawText());
        Assert.All(preview.ResolvedReferences, item => Assert.NotNull(item.Version));
        await session.CommitTransactionAsync();
        Assert.Equal(before, await State());
    }

    [Fact]
    public async Task Reverse_dependencies_include_current_identity_and_reject_changed_complete_set()
    {
        var current = await CreateApp();
        var dependencies = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(Provider);
        var group = new GitLifecycleResourceReference(
            new ResourceAddress("ResourceGroup", "alpha"),
            current.ResourceGroupId);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var found = await dependencies.ReadDependenciesAsync(
            new ReadLifecycleDependenciesRequest(current.TenantId, group),
            session,
            default);
        Assert.Equal(current.Id, Assert.Single(found).Dependent.ResourceId);
        Assert.Empty(
            await dependencies.ReadDependenciesAsync(
                new ReadLifecycleDependenciesRequest(ObjectId.GenerateNewId().ToString(), group),
                session,
                default));
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await dependencies.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(current.TenantId, group, []),
                session,
                default)).Value);
        Assert.IsType<DeclarationFenced>(
            (await dependencies.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(current.TenantId, group, found),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("managed-configuration")]
    public async Task Deleting_intent_ignores_runtime_progress_but_rejects_changed_configuration(string changed)
    {
        var current = await CreateApp();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var active = await Snapshot(current, session);
        Assert.IsType<GitLifecycleRemovalRequested>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, active.Resource, active.Version),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        session.StartTransaction();
        var deleting = await Snapshot(current, session);
        await session.CommitTransactionAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == current.Id).SingleAsync();
        var at = placement.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var started = placement.Start(
            placement.DesiredGeneration,
            ResourceDeploymentOperation.Delete,
            claim,
            at,
            at.AddMinutes(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, started);
        session.StartTransaction();
        Assert.Equal(deleting.Version.Documents, (await Snapshot(current, session)).Version.Documents);
        await session.CommitTransactionAsync();
        var completed = started.Complete(
            started.DesiredGeneration,
            ResourceDeploymentOperation.Delete,
            claim,
            [],
            at.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, completed);
        session.StartTransaction();
        Assert.Equal(deleting.Version.Documents, (await Snapshot(current, session)).Version.Documents);
        Assert.IsType<GitLifecycleRemovalRequested>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, deleting.Resource, deleting.Version),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        var update = changed switch
        {
            "generation" => Builders<ResourceDeployment>.Update.Inc(item => item.DesiredGeneration, 1),
            "resource-revision" => Builders<ResourceDeployment>.Update.Inc(item => item.ResourceRevision, 1),
            _ => Builders<ResourceDeployment>.Update.Set(
                item => item.ResolvedPlacement.Compute.Limits,
                completed.ResolvedPlacement.Compute.Limits with { CpuMillis = 9999 }),
        };
        await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id, update);
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authority.Authority, deleting.Resource, deleting.Version),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    private async Task<FunctionApp> CreateApp()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var result = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "reviewed-functions",
                    "reviewed-functions",
                    group.Id,
                    Revision(),
                    [],
                    Placement: new FunctionPlacementRequest(TestData.DefaultDeploymentTargetId, null)),
                Data.TenantId))).RequireSuccess();
        return await Load(result.FunctionAppId);
    }

    private Task<FunctionApp> Load(string id) => Apps.Find(item => item.Id == id).SingleAsync();

    private async Task<GitLifecycleResourceSnapshot> Snapshot(FunctionApp current, IClientSessionHandle session) =>
        Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(
                new ReadLifecycleResourceRequest(
                    current.TenantId,
                    new GitLifecycleResourceReference(
                        new ResourceAddress("FunctionApp", current.NormalizedName),
                        current.Id)),
                session,
                default)).Value);

    private async Task<string> State()
    {
        var quota = Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>()
            .CollectionNamespace.CollectionName;
        string[] names =
        [
            "FunctionApps", "FunctionRevisions", "FunctionRouteBindings", "FunctionTimerStates",
            "ResourceDeployments", "ResourceConnections", quota, "OutboxMessages",
        ];
        var result = new List<string>();
        foreach (var name in names)
        {
            var documents = await Fixture.Database
                .GetCollection<BsonDocument>(name)
                .Find(_ => true)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync();
            result.AddRange(documents.Select(item => item.ToJson()));
        }

        return string.Join("\n", result);
    }

    private static FunctionRevisionRequest Revision() => new(
        new FunctionImageRequest("registry.example", "team/functions", "v1"),
        new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty),
        new FunctionResources(new FunctionResourceValues(100, 128), new FunctionResourceValues(500, 512)),
        new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
        [
            new FunctionHandlerRequest(
                "cleanup",
                1,
                new TimerFunctionTriggerRequest(
                    "*/5 * * * *",
                    TimerConcurrencyPolicy.Forbid,
                    TimerMisfirePolicy.FireOnce,
                    new FunctionRetryPolicy(3, [5, 30]))),
        ]);

    private static CompiledDeclaration Declaration(FunctionApp current)
    {
        var revision = Revision();
        var spec = new FunctionDeclaration
        {
            ResourceGroup = "alpha",
            FunctionRoute = current.FunctionRoute,
            Placement = new FunctionDeclarationPlacement("lab", new FunctionPinnedProfile("small", 1), null),
            Image = new FunctionDeclarationImage("registry.example", "team/functions", "v1"),
            Resources = revision.Resources,
            Scaling = revision.Scaling,
            Handlers =
            [
                new FunctionDeclarationHandler(
                    "cleanup",
                    1,
                    new FunctionDeclarationTimerTrigger(
                        "*/5 * * * *",
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        new FunctionRetryPolicy(3, [5, 30]))),
            ],
        };
        var options = new JsonSerializerOptions(DeclarationJson.Options)
        {
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };
        return new CompiledDeclaration(
            new ResourceAddress("FunctionApp", current.NormalizedName),
            JsonSerializer.SerializeToElement(spec, options),
            "desired",
            [],
            "function.yaml",
            1);
    }
}
