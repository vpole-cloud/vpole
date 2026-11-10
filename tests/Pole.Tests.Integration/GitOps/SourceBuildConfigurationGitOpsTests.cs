using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SourceBuildConfigurationGitOpsTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task BuildRecipeDriftRepairsWithoutSchedulingBuildAndThenNoOps()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var configurations = DbContext.SourceBuildConfigurations;
        var current = await configurations
            .Find(item => item.TenantId == Data.TenantId && item.TechnicalName == "application-build")
            .SingleAsync();
        await configurations.ReplaceOneAsync(
            item => item.Id == current.Id,
            current with
            {
                Recipe = new SourceBuildRecipe("FROM drift"),
                Revision = current.Revision + 1,
            });

        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await configurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(current.Recipe, repaired.Recipe);
        Assert.Equal(current.Revision + 2, repaired.Revision);
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await Releases());
        var events = await BusinessEvents();

        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var unchanged = await configurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(repaired.Revision, unchanged.Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task DeclaredRecipeUpdateChangesOnlyConfigurationRevisionWithoutStartingBuild()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var current = await DbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == Data.TenantId && item.TechnicalName == "application-build")
            .SingleAsync();
        ManifestFile[] changed = [files[0] with { Content = files[0].Content.Replace("FROM scratch", "FROM busybox") }];
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(changed)).Status);
        var updated = await DbContext.SourceBuildConfigurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal("FROM busybox", updated.Recipe.Dockerfile);
        Assert.Equal(current.Revision + 1, updated.Revision);
        Assert.Equal(current.SourceRepositoryId, updated.SourceRepositoryId);
        Assert.Equal(current.WebServiceId, updated.WebServiceId);
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await Releases());
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(
            (await WebServices.Find(item => item.Id == current.WebServiceId).SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task MissingCreatedBuildConfigurationIsBlockedWithoutRecreation()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var current = await DbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == Data.TenantId && item.TechnicalName == "application-build")
            .SingleAsync();
        await DbContext.SourceBuildConfigurations.DeleteOneAsync(item => item.Id == current.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(
            0,
            await DbContext.SourceBuildConfigurations.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await Releases());
    }
}
