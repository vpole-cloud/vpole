using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Domain;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.GitOps;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

[Collection(IntegrationTestCollection.Global4)]
public sealed class WebServiceLifecycleTests(MongoReplicaSetFixture fixture) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private LifecycleOwnerAuthority? _authority;
    private LifecycleOwnerAuthority authority => _authority ??= new(Data.TenantId);
    private IMongoCollection<WebService> WebServices => Fixture.Database.GetCollection<WebService>("WebServices");
    private IDeclarativeResourceLifecycleProvider Provider =>
        Services.GetServices<IDeclarativeResourceLifecycleProvider>()
        .Single(item => item.Kind == "WebService");

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.RemoveAll<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>();
        services.AddScoped<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(provider =>
            provider.GetRequiredService<MongoListEligiblePublicIngressDeploymentTargetsForNewWorkload>().InvokeAsync);
        services.RemoveAll<AdmitDeploymentTargetPublicIngressForNewWorkload>();
        services.AddScoped<AdmitDeploymentTargetPublicIngressForNewWorkload>(provider =>
            provider.GetRequiredService<MongoAdmitDeploymentTargetPublicIngressForNewWorkload>().InvokeAsync);
        services.AddSingleton<IGitOpsMutations>(authority);
        services.AddSingleton<IGitLifecyclePlanGuard>(authority);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Fixture.PrepareNamedCatalogAsync();
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        var configuration = target.WorkloadConfiguration.Reconfigure(
            DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt", "apps.example.test"),
            new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("public-ingress",
                DeploymentTargetPodNetworkSource.Create("ingress",
                    new Dictionary<string, string> { ["app"] = "ingress" }))));
        await targets.ReplaceOneAsync(item => item.Id == target.Id,
            target with { WorkloadConfiguration = configuration });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_deletion_preserves_git_owned_bindings_and_allows_unowned_bindings(bool gitOwned)
    {
        var current = await CreateService();
        var group = await DbContext.ResourceGroups.Find(_ => true).SingleAsync();
        var secret = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group,
            WebServiceResourceDeployment.ResourceType, current.Id);
        if (gitOwned)
            authority.Managed.Add(("SecretBinding", secret.Bindings.Single().Id));
        var before = await State();
        var deleted = Assert.IsType<DeleteWebServiceOutcome>((await Mediator.Send(
            new DeleteWebServiceRequest(current.Id, current.TenantId))).Value);
        if (gitOwned)
        {
            var error = Assert.IsType<DeleteWebServiceError>(deleted.Value);
            Assert.Equal(WebServiceUpdateConflictReason.SecretBindingsRequireRemoval,
                Assert.IsType<WebServiceUpdateConflict>(error.Value).Reason);
            Assert.Equal(409, WebServiceHttpMappings.MapError(error).StatusCode);
            Assert.Equal(before, await State());
            using var session = await Fixture.Client.StartSessionAsync();
            session.StartTransaction();
            var snapshot = await Snapshot(current, session);
            Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<DeclarationReadBlocked>((await
                Provider.RemoveAsync(new(authority.Authority, snapshot.Resource, snapshot.Version), session,
                    default)).Value).Failure);
            await session.AbortTransactionAsync();
            Assert.Equal(before, await State());
        }
        else
            Assert.IsType<DeleteWebServiceResponse>(deleted.Value);
        Assert.Single((await Fixture.Database.GetCollection<Secret>("Secrets")
            .Find(item => item.Id == secret.Id).SingleAsync()).Bindings);
    }

    [Fact]
    public async Task Removal_joins_quota_placement_and_events_and_waits_for_owner_finalization()
    {
        var current = await CreateService();
        var before = await State();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(
            new(authority.Authority, snapshot.Resource, snapshot.Version), session, default)).Value);
        Assert.Equal(GitLifecycleMutationEffect.Remove, authority.Effect);
        await session.AbortTransactionAsync();
        Assert.Equal(before, await State());

        session.StartTransaction();
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(
            new(authority.Authority, snapshot.Resource, snapshot.Version), session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.IsType<DeletingWebServiceLifecycle>((await Load(current.Id)).Lifecycle);
        Assert.All(await DbContext.ResourceDeployments.Find(item => item.ResourceId == current.Id).ToListAsync(),
            item => Assert.Equal(ResourceDeploymentOperation.Delete, item.DesiredOperation));
        var after = await State();
        session.StartTransaction();
        var pending = await Snapshot(current, session);
        Assert.Equal(GitLifecyclePresence.Deleting, pending.Presence);
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(
            new(authority.Authority, pending.Resource, pending.Version), session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(after, await State());
    }

    [Fact]
    public async Task Unreviewed_connection_blocks_cascade_and_all_owner_changes()
    {
        var current = await CreateService();
        var create = Services.GetRequiredService<PersistResourceConnectionCreation>();
        var connection = Assert.IsType<ResourceConnection>((await create(
            new(current.TenantId, "self", WebServiceResourceDeployment.ResourceType, current.Id,
                WebServiceResourceDeployment.ResourceType, current.Id, "http"))).Value);
        var before = await State();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        var result = await Provider.RemoveAsync(new(authority.Authority, snapshot.Resource, snapshot.Version), session,
            default);
        Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<DeclarationReadBlocked>(result.Value).Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(before, await State());
        var connections = Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");
        Assert.IsType<ActiveResourceConnection>((await connections
            .Find(item => item.Id == connection.Id).SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task Changed_managed_configuration_without_a_revision_is_still_a_stale_approval()
    {
        var current = await CreateService();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        await session.CommitTransactionAsync();
        await WebServices.ReplaceOneAsync(item => item.Id == current.Id, current with
        {
            Spec = current.Spec with
            {
                Config = current.Spec.Config with
                {
                    EnvironmentVariables = current.Spec.Config.EnvironmentVariables.SetItem("CHANGED", "value")
                }
            }
        });
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>((await Provider.RemoveAsync(
            new(authority.Authority, snapshot.Resource, snapshot.Version), session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.IsType<ActiveWebServiceLifecycle>((await Load(current.Id)).Lifecycle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("own")]
    [InlineData("dependency")]
    public async Task Preview_and_adoption_preserve_server_credentials_identity_and_business_revision(string? change)
    {
        var current = await CreateService();
        current = current with
        {
            Spec = current.Spec with
            {
                Image = Assert.IsType<ImageInfo>(current.Spec.Image) with { SecretName = "server-owned-pull-secret" }
            }
        };
        await WebServices.ReplaceOneAsync(item => item.Id == current.Id, current);
        var declaration = Declaration(current);
        authority.References[new("ResourceGroup", "alpha")] = current.ResourceGroupId;
        authority.References[new("DeploymentTarget", NamedCatalogTestData.TargetName)] =
            TestData.DefaultDeploymentTargetId;
        authority.References[new("ComputeProfile", NamedCatalogTestData.ProfileName)] =
            TestData.DefaultComputeProfileId;
        var before = await State();
        var previewProvider = Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(Provider);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>((await previewProvider.PreviewAdoptionAsync(
            new(current.TenantId, declaration, current.Id,
                authority.References.Select(item => new GitPreviewResolvedReference(item.Key, item.Value)).ToArray()),
            session, default)).Value);
        Assert.DoesNotContain("server-owned-pull-secret", preview.Current.Projection.Configuration.GetRawText());
        Assert.Contains(preview.ResolvedReferences, item => item.Address.Kind == "ComputeProfile" &&
            item.ResourceId == TestData.DefaultComputeProfileId && item.Version is not null);
        await session.CommitTransactionAsync();
        Assert.Equal(before, await State());

        session.StartTransaction();
        var snapshot = await Snapshot(current, session);
        var context = new GitMutationContext(current.TenantId, authority.Authority.SourceId, authority.Authority.RunId,
            1, "https://example.test/source", new(1, 1, "worker", 1, new string('a', 40)));
        var operation = new GitOperationInput(context, declaration, DeclarationPhase.Prepare, current.Id);
        var adoption = Assert.IsAssignableFrom<IDeclarativeResourceAdoptionProvider>(Provider);
        var expected = new DeclarationVersionToken(snapshot.Version.Documents
            .Concat(preview.Desired.Dependencies!.Documents).Distinct().ToArray());
        if (change is not null)
        {
            var changedDocument = change == "own"
                ? $"WebService/{current.Id}"
                : $"ComputeProfile/{TestData.DefaultComputeProfileId}/Policy";
            expected = new(expected.Documents.Select(item => item.DocumentId == changedDocument
                ? item with { Revision = item.Revision + 1 } : item).ToArray());
            Assert.IsType<DeclarationPreconditionsChanged>((await adoption.AdoptAsync(
                new(authority.Authority, operation, expected), session, default)).Value);
            await session.AbortTransactionAsync();
            Assert.True(before == await State(), "Rejected adoption must roll back owner and dependency writes.");
            return;
        }

        Assert.IsType<GitLifecycleResourceAdopted>((await adoption.AdoptAsync(
            new(authority.Authority, operation, expected), session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(GitLifecycleMutationEffect.Adopt, authority.Effect);
        var adopted = await Load(current.Id);
        Assert.Equal(current.Id, adopted.Id);
        Assert.Equal(current.Revision, adopted.Revision);
        Assert.True(current.Spec.HasSameConfiguration(adopted.Spec));
        Assert.Equal("server-owned-pull-secret", Assert.IsType<ImageInfo>(adopted.Spec.Image).SecretName);
        Assert.Equal(NamedCatalogTestData.TargetName, adopted.DeclaredPlacement!.DeploymentTargetName);
        Assert.Equal(current.ActiveRevision, adopted.ActiveRevision);
    }

    [Fact]
    public async Task Missing_named_scope_cannot_be_rebound_to_the_groups_default_scope()
    {
        var current = await CreateService();
        var declaration = Declaration(current);
        var spec = DeclarationJson.Read<WebServiceDeclaration>(declaration.Spec);
        declaration = declaration with
        {
            Spec = DeclarationJson.Write(spec with { Placement = spec.Placement with { IsolationScope = "missing" } })
        };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var references = Assert.IsAssignableFrom<IDeclarativePreviewReferenceProvider>(Provider);
        var result = await references.ReadPreviewReferencesAsync(new(current.TenantId, declaration,
            [new(new("ResourceGroup", "alpha"), current.ResourceGroupId)]), session, default);
        var unavailable = Assert.IsType<DeclarationReferenceUnavailable>(
            Assert.IsType<DeclarationReadBlocked>(result.Value).Failure);
        Assert.Equal(new("IsolationScope", "missing"), unavailable.Address);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Manual_delete_protects_git_owned_attachments_without_changing_unowned_cleanup(
        bool managed, bool orphan)
    {
        var current = await CreateService();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == current.Id).SingleAsync();
        var zone = await Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones")
            .Find(item => item.TenantId == current.TenantId).SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var binding = Assert.IsType<HostnameBindingSnapshot>((await Services
            .GetRequiredService<CreateCustomHostnameBinding>()(new(ObjectId.GenerateNewId().ToString(),
                current.TenantId, zone.Id, placement.ResolvedPlacement.DeploymentTargetId,
                WebServiceResourceDeployment.ResourceType,
                current.Id, orphan ? ObjectId.GenerateNewId().ToString() : placement.Id,
                "custom", "custom"), session)).Value);
        await session.CommitTransactionAsync();
        if (managed)
            authority.Managed.Add(("HostnameBinding", binding.Id));
        var before = await State();
        var result = (await Mediator.Send(new DeleteWebServiceRequest(current.Id, current.TenantId)))
            .AssertSuccess<DeleteWebServiceOutcome>();
        if (managed)
        {
            var error = Assert.IsType<DeleteWebServiceError>(result.Value);
            Assert.Equal(binding.Id, Assert.IsType<WebServiceHostnameAttachmentRemovalRequired>(error.Value).BindingId);
            Assert.Equal(409, WebServiceHttpMappings.MapError(error).StatusCode);
            Assert.Equal(before, await State());
        }
        else
        {
            Assert.IsType<DeleteWebServiceResponse>(result.Value);
            Assert.IsType<DeletingWebServiceLifecycle>((await Load(current.Id)).Lifecycle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Image_and_build_configuration_dependencies_allow_staged_removal_without_waiting_for_cleanup(
        bool awaitingArtifact)
    {
        var current = await CreateService();
        var configuration = SourceBuildConfigurationFactory.Create(current.TenantId, "reviewed-build",
            ObjectId.GenerateNewId().ToString(), current.Id, SourceBuildRecipe.Create("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin(Data.UserId), SourceBuildConfigurationFactory.DefaultOptions);
        await Fixture.Database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations")
            .InsertOneAsync(configuration);
        var image = awaitingArtifact
            ? (WebServiceImage)WebServiceImageFactory.Pending(configuration.Id)
            : DeliveredSourceBuildImage.Create(configuration.Id, Assert.IsType<ImageInfo>(current.Spec.Image),
                "sha256:" + new string('a', 64), ObjectId.GenerateNewId().ToString(), 1);
        current = current with
        {
            Spec = current.Spec with { Image = image },
            Lifecycle = awaitingArtifact ? new AwaitingFirstArtifactWebServiceLifecycle() : current.Lifecycle
        };
        await WebServices.ReplaceOneAsync(item => item.Id == current.Id, current);
        var webDependencies = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(Provider);
        var buildLifecycle = Services.GetServices<IDeclarativeResourceLifecycleProvider>()
            .Single(item => item.Kind == "SourceBuildConfiguration");
        var buildDependencies = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(buildLifecycle);
        var web = new GitLifecycleResourceReference(new("WebService", current.Name), current.Id);
        var build = new GitLifecycleResourceReference(new("SourceBuildConfiguration", configuration.TechnicalName),
            configuration.Id);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var imageEdge = Assert.Single(await webDependencies.ReadDependenciesAsync(new(current.TenantId, build),
            session, default));
        Assert.Equal(web, imageEdge.Dependent);
        Assert.Equal(GitLifecycleDependencyOrder.AfterDependentRemovalRequested, imageEdge.Order);
        var buildEdge = Assert.Single(await buildDependencies.ReadDependenciesAsync(new(current.TenantId, web),
            session, default));
        Assert.Equal(build, buildEdge.Dependent);
        Assert.Equal(GitLifecycleDependencyOrder.AfterRemovalRequested, buildEdge.Order);
        Assert.IsType<DeclarationPreconditionsChanged>((await webDependencies.FenceDependenciesAsync(
            new(current.TenantId, build, [imageEdge with { Order = GitLifecycleDependencyOrder.BeforeRemoval }]),
            session, default)).Value);
        var bound = Assert.IsType<GitLifecycleResourceSnapshot>((await buildLifecycle.ReadLifecycleAsync(
            new(current.TenantId, build), session, default)).Value);
        var beforeBegin = Assert.IsType<DeclarationReadBlocked>((await buildLifecycle.RemoveAsync(
            new(authority.Authority, build, bound.Version), session, default)).Value);
        Assert.IsType<GitLifecycleDependencyRetained>(beforeBegin.Failure);
        await session.AbortTransactionAsync();

        var deleting = current.BeginDeletion(DateTime.UtcNow);
        await WebServices.ReplaceOneAsync(item => item.Id == current.Id, deleting);
        session.StartTransaction();
        Assert.Empty(await webDependencies.ReadDependenciesAsync(new(current.TenantId, build), session, default));
        Assert.IsType<DeclarationPreconditionsChanged>((await webDependencies.FenceDependenciesAsync(
            new(current.TenantId, build, [imageEdge]), session, default)).Value);
        Assert.Single(await buildDependencies.ReadDependenciesAsync(new(current.TenantId, web), session, default));
        Assert.IsType<SourceBuildWebServiceNotFound>((await Services.GetRequiredService<LoadSourceBuildTarget>()(
            new(current.TenantId, current.Id), session, default)).Value);
        Assert.Single(await webDependencies.ReadDependenciesAsync(new(current.TenantId,
            new(new("ResourceGroup", "alpha"), current.ResourceGroupId)), session, default));
        Assert.Single(await webDependencies.ReadDependenciesAsync(new(current.TenantId,
            new(new("IsolationScope", "default"), current.IsolationScopeId)), session, default));
        var ready = Assert.IsType<GitLifecycleResourceSnapshot>((await buildLifecycle.ReadLifecycleAsync(
            new(current.TenantId, build), session, default)).Value);
        Assert.IsType<GitLifecycleResourceRemoved>((await buildLifecycle.RemoveAsync(
            new(authority.Authority, build, ready.Version), session, default)).Value);
        Assert.Empty(await buildDependencies.ReadDependenciesAsync(new(current.TenantId, web), session, default));
        Assert.Equal(GitLifecyclePresence.Deleting, (await Snapshot(current, session)).Presence);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("managed-configuration")]
    public async Task Deleting_intent_ignores_runtime_progress_but_rejects_changed_configuration(string changed)
    {
        var current = await CreateService();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var active = await Snapshot(current, session);
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(
            new(authority.Authority, active.Resource, active.Version), session, default)).Value);
        await session.CommitTransactionAsync();
        session.StartTransaction();
        var deleting = await Snapshot(current, session);
        await session.CommitTransactionAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == current.Id).SingleAsync();
        var at = placement.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var started = placement.Start(placement.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, at, at.AddMinutes(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, started);
        session.StartTransaction();
        Assert.Equal(deleting.Version.Documents, (await Snapshot(current, session)).Version.Documents);
        await session.CommitTransactionAsync();
        var completed = started.Complete(started.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, [], at.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, completed);
        session.StartTransaction();
        Assert.Equal(deleting.Version.Documents, (await Snapshot(current, session)).Version.Documents);
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(
            new(authority.Authority, deleting.Resource, deleting.Version), session, default)).Value);
        await session.CommitTransactionAsync();
        var update = changed switch
        {
            "generation" => Builders<ResourceDeployment>.Update.Inc(item => item.DesiredGeneration, 1),
            "resource-revision" => Builders<ResourceDeployment>.Update.Inc(item => item.ResourceRevision, 1),
            _ => Builders<ResourceDeployment>.Update.Set(item => item.ResolvedPlacement.Compute.Limits,
                completed.ResolvedPlacement.Compute.Limits with { CpuMillis = 9999 })
        };
        await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id, update);
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>((await Provider.RemoveAsync(
            new(authority.Authority, deleting.Resource, deleting.Version), session, default)).Value);
        await session.AbortTransactionAsync();
    }

    private async Task<WebService> CreateService()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var outcome = (await Mediator.Send(Data.CreateWebServiceRequest(group.Id, "reviewed")))
            .AssertSuccess<CreateWebServiceOutcome>();
        if (outcome is CreateWebServiceError error)
        {
            var category = error.Value.GetType().Name;
            if (error.Value is CreateWebServicePlacementError placement)
                category += "/" + placement.Value.GetType().Name;
            if (error.Value is GeneratedDomainZoneError zone)
                category += "/" + zone.Value.GetType().Name;
            throw new Xunit.Sdk.XunitException("Fixture creation rejected: " + category);
        }
        return await Load(Assert.IsType<CreateWebServiceResponse>(outcome.Value).WebServiceId);
    }

    private Task<WebService> Load(string id) => WebServices.Find(item => item.Id == id).SingleAsync();

    private async Task<GitLifecycleResourceSnapshot> Snapshot(WebService current, IClientSessionHandle session) =>
        Assert.IsType<GitLifecycleResourceSnapshot>((await Provider.ReadLifecycleAsync(new(current.TenantId,
            new(new("WebService", current.Name), current.Id)), session, default)).Value);

    private async Task<string> State()
    {
        var quotaCollection = Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>()
            .CollectionNamespace.CollectionName;
        string[] names = ["WebServices", "ResourceDeployments", quotaCollection, "OutboxMessages"];
        var result = new List<string>();
        foreach (var name in names)
        {
            var documents = await Fixture.Database.GetCollection<BsonDocument>(name).Find(_ => true)
                .Sort(new BsonDocument("_id", 1)).ToListAsync();
            result.AddRange(documents.Select(item => item.ToJson()));
        }
        return string.Join("\n", result);
    }

    private static CompiledDeclaration Declaration(WebService current)
    {
        var defaults = TestData.ValidWebServiceSpec();
        var spec = new WebServiceDeclaration
        {
            ResourceGroup = "alpha",
            Image = new DeclaredContainerImage("docker.io", "nginx", "1.27"),
            Placement = new(NamedCatalogTestData.TargetName, new(NamedCatalogTestData.ProfileName, 1)),
            Ports = defaults.Config.PortMappings,
            Probes = defaults.Probes,
            Replicas = 2,
            Exposure = "Public",
            Environment = defaults.Config.EnvironmentVariables
        };
        return new(new("WebService", current.Name), DeclarationJson.Write(spec), "desired", [], "service.yaml", 1);
    }
}
