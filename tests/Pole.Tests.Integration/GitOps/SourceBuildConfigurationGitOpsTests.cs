using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task BuildRecipeDriftRepairsWithoutSchedulingBuildAndThenNoOps()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var configurations = DbContext.SourceBuildConfigurations;
        var current = await configurations.Find(item => item.TechnicalName == "application-build").SingleAsync();
        await configurations.ReplaceOneAsync(item => item.Id == current.Id, current with
        {
            Recipe = new("FROM drift"),
            Revision = current.Revision + 1
        });

        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await configurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(current.Recipe, repaired.Recipe);
        Assert.Equal(current.Revision + 2, repaired.Revision);
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Releases());
        var events = await BusinessEvents();

        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var unchanged = await configurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal(repaired.Revision, unchanged.Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task DeclaredRecipeUpdateChangesOnlyConfigurationRevisionWithoutStartingBuild()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var current = await DbContext.SourceBuildConfigurations.Find(item => item.TechnicalName == "application-build")
            .SingleAsync();
        ManifestFile[] changed = [files[0] with { Content = files[0].Content.Replace("FROM scratch", "FROM busybox") }];
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(changed)).Status);
        var updated = await DbContext.SourceBuildConfigurations.Find(item => item.Id == current.Id).SingleAsync();
        Assert.Equal("FROM busybox", updated.Recipe.Dockerfile);
        Assert.Equal(current.Revision + 1, updated.Revision);
        Assert.Equal(current.SourceRepositoryId, updated.SourceRepositoryId);
        Assert.Equal(current.WebServiceId, updated.WebServiceId);
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Releases());
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>((await WebServices
            .Find(item => item.Id == current.WebServiceId).SingleAsync()).Lifecycle);
    }

    [Fact]
    public async Task MissingCreatedBuildConfigurationIsBlockedWithoutRecreation()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var current = await DbContext.SourceBuildConfigurations.Find(item => item.TechnicalName == "application-build")
            .SingleAsync();
        await DbContext.SourceBuildConfigurations.DeleteOneAsync(item => item.Id == current.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(0, await DbContext.SourceBuildConfigurations.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Releases());
    }
}
