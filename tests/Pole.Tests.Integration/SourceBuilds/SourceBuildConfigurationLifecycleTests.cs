using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildConfigurationLifecycleTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private readonly ReviewedLifecycleAuthority authorization = new(fixture.Client);
    private SourceBuildTargetSnapshot? previewTarget;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IGitOpsMutations>(authorization);
        services.AddSingleton<LoadSourceBuildTarget>((request, _, _) => Task.FromResult<LoadSourceBuildTargetOutcome>(
            previewTarget is { } current && current.TenantId == request.TenantId &&
            current.WebServiceId == request.WebServiceId ? current : new SourceBuildWebServiceNotFound(
                request.WebServiceId)));
    }
    private IMongoCollection<SourceBuildConfiguration> Configurations => Fixture.Database
        .GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations");
    private IDeclarativeResourceLifecycleProvider Provider => Services
        .GetServices<IDeclarativeResourceLifecycleProvider>()
        .Single(item => item.Kind == "SourceBuildConfiguration");

    [Fact]
    public async Task Unbound_configuration_removal_is_atomic_with_outbox()
    {
        var configuration = Configuration();
        await Configurations.InsertOneAsync(configuration);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await Provider.ReadLifecycleAsync(
            Read(configuration), session, default)).Value);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>((await Provider.RemoveAsync(request, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(1, await Configurations.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext
            .CountOutboxMessagesAsync("pole.source-builds/v1/source-build-configuration-changed"));
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>((await Provider.RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(0, await Configurations.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Active_build_blocks_deletion_without_changing_configuration()
    {
        var configuration = Configuration();
        await Configurations.InsertOneAsync(configuration);
        var job = SourceBuildJobFactory.CreateManual(configuration.TenantId, configuration.Id,
            configuration.SourceRepositoryId,
            configuration.WebServiceId, "main", new string('a', 40), new(1, 1, 1), configuration.Recipe,
            TestData.UserId, null, SourceBuildJobFactory.DefaultOptions);
        await Fixture.Database.GetCollection<SourceBuildJob>("SourceBuildJobs").InsertOneAsync(job);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await Provider.ReadLifecycleAsync(
            Read(configuration), session, default)).Value);
        var blocked = Assert.IsType<DeclarationReadBlocked>((await Provider.RemoveAsync(
            new(authorization.Authority, snapshot.Resource, snapshot.Version), session, default)).Value);
        Assert.IsType<GitLifecycleDependencyRetained>(blocked.Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(0, (await Configurations.Find(_ => true).SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task Reverse_dependencies_distinguish_repository_order_from_workload_removal_request()
    {
        var configuration = Configuration();
        await Configurations.InsertOneAsync(configuration);
        var provider = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(Provider);
        using var session = await Fixture.Client.StartSessionAsync();
        var repository = Assert.Single(await provider.ReadDependenciesAsync(new(configuration.TenantId,
            new(new("SourceRepository", "repository"), configuration.SourceRepositoryId)), session, default));
        Assert.Equal(GitLifecycleDependencyOrder.BeforeRemoval, repository.Order);
        var workload = Assert.Single(await provider.ReadDependenciesAsync(new(configuration.TenantId,
            new(new("WebService", "service"), configuration.WebServiceId)), session, default));
        Assert.Equal(GitLifecycleDependencyOrder.AfterRemovalRequested, workload.Order);
        Assert.Equal(configuration.Id, workload.Dependent.ResourceId);
    }

    [Fact]
    public async Task Adoption_preview_uses_existing_ids_and_does_not_write_recipe_or_queue_jobs()
    {
        var connection = GitProviderConnectionFactory.Create(TestData.TenantId, "github-app", GitProviderKind.GitHub,
            "installation", "account", "login", GitProviderAccountType.User, GitProviderRepositorySelection.All,
            TestData.UserId, GitProviderConnectionFactory.DefaultOptions);
        await Fixture.Database.GetCollection<GitProviderConnection>("GitProviderConnections")
            .InsertOneAsync(connection);
        var repository = SourceRepositoryFactory.Create(TestData.TenantId, "repository", connection.Id, "42",
            "owner/application", "https://example.test/owner/application", true, "main",
            SourceRepositoryFactory.DefaultOptions);
        await Fixture.Database.GetCollection<SourceRepository>("SourceRepositories").InsertOneAsync(repository);
        var configuration = Configuration() with { SourceRepositoryId = repository.Id };
        await Configurations.InsertOneAsync(configuration);
        previewTarget = new(configuration.TenantId, configuration.WebServiceId, 1, configuration.Id);
        var declaration = new CompiledDeclaration(Read(configuration).Resource.Address, DeclarationJson.Write(new
        {
            repository = repository.TechnicalName,
            webService = "service",
            recipe = new { dockerfile = "FROM scratch\nLABEL version=2" }
        }), "hash", [], "build.yaml", 1);
        GitPreviewResolvedReference[] references =
        [
            new(new("SourceRepository", repository.TechnicalName), repository.Id),
            new(new("WebService", "service"), configuration.WebServiceId)
        ];
        var provider = Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(Provider);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>((await provider.PreviewAdoptionAsync(
            new(configuration.TenantId, declaration, configuration.Id, references), session, default)).Value);
        Assert.Equal(configuration.Id, preview.Current.ResourceId);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(preview.Current.Projection.Identity,
            preview.Desired.Identity));
        Assert.False(System.Text.Json.JsonElement.DeepEquals(preview.Current.Projection.Configuration,
            preview.Desired.Configuration));
        var rejected = await provider.PreviewAdoptionAsync(new(configuration.TenantId, declaration, configuration.Id,
            [references[0], references[1] with { ResourceId = ObjectId.GenerateNewId().ToString() }]),
            session, default);
        Assert.IsType<DeclarationReadBlocked>(rejected.Value);
        await session.CommitTransactionAsync();
        var stored = await Configurations.Find(_ => true).SingleAsync();
        Assert.Equal(configuration.Recipe, stored.Recipe);
        Assert.Equal(configuration.Revision, stored.Revision);
        Assert.Equal(configuration.ReferenceFence, stored.ReferenceFence);
        Assert.Equal(configuration.Origin, stored.Origin);
        Assert.Equal(0, await Fixture.Database.GetCollection<SourceBuildJob>("SourceBuildJobs")
            .CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext
            .CountOutboxMessagesAsync("pole.source-builds/v1/source-build-configuration-changed"));
    }

    [Fact]
    public async Task Workload_cleanup_preserves_git_owned_configuration_and_cleans_manual_configuration()
    {
        var owned = Configuration();
        var manual = Configuration() with { TechnicalName = "manual" };
        await Configurations.InsertManyAsync([owned, manual]);
        authorization.GitOwnedResources.Add(owned.Id);
        await Services.GetRequiredService<CleanupSourceBuildConfigurations>()(owned.TenantId,
            owned.WebServiceId, default);
        Assert.Equal(2, await Configurations.CountDocumentsAsync(_ => true));
        await Services.GetRequiredService<CleanupSourceBuildConfigurations>()(manual.TenantId,
            manual.WebServiceId, default);
        Assert.Equal(owned.Id, (await Configurations.Find(_ => true).SingleAsync()).Id);
        Assert.Equal(1, await DbContext
            .CountOutboxMessagesAsync("pole.source-builds/v1/source-build-configuration-changed"));
    }

    private static SourceBuildConfiguration Configuration() => SourceBuildConfigurationFactory.Create(TestData.TenantId,
        "application-build", ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
        SourceBuildRecipe.Create("FROM scratch"), new ManualSourceBuildConfigurationOrigin(TestData.UserId),
        SourceBuildConfigurationFactory.DefaultOptions);
    private static ReadLifecycleResourceRequest Read(SourceBuildConfiguration item) => new(item.TenantId,
        new(new("SourceBuildConfiguration", item.TechnicalName), item.Id));
}
