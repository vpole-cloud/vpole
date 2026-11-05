using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private static void ConfigureSourceDeliveryServices(IServiceCollection services)
    {
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());
        services.AddSingleton<ResolveProviderRepository>((request, _) =>
            Task.FromResult<ResolveProviderRepositoryOutcome>(new ResolvedProviderRepository(
                new(request.RepositoryId, "application", "organization/application",
                    "https://github.test/organization/application", true, request.Branch, null), new string('a', 40))));
    }

    private static ManifestFile[] SourceDeliverySnapshot()
    {
        var documents = StageFiveWebSnapshot(spec => spec["image"] =
            new JsonObject { ["sourceBuildConfiguration"] = "application-build" })[0].Content
            .Split("\n---\n").Select(item => JsonNode.Parse(item)!).ToList();
        documents.Add(JsonNode.Parse("""
            {"apiVersion":"pole.io/v1alpha1","kind":"SourceRepository","metadata":{"name":"application"},
             "spec":{"connection":"production","repositoryId":"42","branch":"main"}}
            """)!);
        documents.Add(JsonNode.Parse("""
            {"apiVersion":"pole.io/v1alpha1","kind":"SourceBuildConfiguration",
             "metadata":{"name":"application-build"},"spec":{"repository":"application",
             "webService":"demo-api","recipe":{"dockerfile":"FROM scratch"}}}
            """)!);
        return [new("source-delivery.yaml", string.Join("\n---\n", documents.Select(item => item.ToJsonString())))];
    }

    private Task SeedApplicationConnectionAsync() => DbContext.GitProviderConnections.InsertOneAsync(
        GitProviderConnectionFactory.Create(TestData.TenantId, "production", GitProviderKind.GitHub, "42",
            "account", "organization", GitProviderAccountType.Organization, GitProviderRepositorySelection.All,
            TestData.UserId, GitProviderConnectionFactory.DefaultOptions));

    [Fact]
    public async Task BuildOwnedWebServiceAcceptsConfigurationWithoutArtifactAndThenNoOps()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await WebServices.Find(item => item.Name == "demo-api").SingleAsync();
        var configuration = await DbContext.SourceBuildConfigurations.Find(item => item.WebServiceId == service.Id)
            .SingleAsync();
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(service.Lifecycle);
        Assert.Equal(configuration.Id, Assert.IsType<PendingSourceBuildImage>(service.Spec.Image).ConfigurationId);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(placement.State);
        Assert.Equal(service.ActiveRevision, placement.ResourceRevision);
        Assert.Equal(0, await Releases());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));

        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var unchanged = await WebServices.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, unchanged.Revision);
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(unchanged.Lifecycle);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(0, await Releases());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task ArtifactPromotionIsAtomicAndIdempotentAndGitOpsPreservesDeliveredImage()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await WebServices.Find(item => item.Name == "demo-api").SingleAsync();
        var configuration = await DbContext.SourceBuildConfigurations.Find(item => item.WebServiceId == service.Id)
            .SingleAsync();
        var application = await DbContext.SourceRepositories.Find(item => item.Id == configuration.SourceRepositoryId)
            .SingleAsync();
        await DbContext.SourceBuildConfigurations.UpdateOneAsync(item => item.Id == configuration.Id,
            Builders<SourceBuildConfiguration>.Update.Set(item => item.BuildGeneration, 1));
        var now = DateTime.UtcNow;
        var job = SourceBuildJobFactory.CreateManual(TestData.TenantId, configuration.Id, application.Id, service.Id,
            application.Branch, new string('a', 40), new(configuration.Revision, application.Revision, 1),
            configuration.Recipe, TestData.UserId, "builder@example.test", SourceBuildJobFactory.DefaultOptions) with
        { Execution = new ClaimedSourceBuildExecution("source-delivery-test", 1, now.AddMinutes(2)) };
        var digest = "sha256:" + new string('d', 64);
        job = job.StartCloning(now).StartAnalyzing(now).StartBuilding(now)
            .StartPushing(SourceBuildArtifactFactory.Create("registry.test/tenant/application:attempt-one", digest),
                now);
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        var request = new SourceBuildDeploymentRequest(TestData.TenantId, service.Id, job.Id, application.Id,
            job.CommitSha, new(configuration.Id, configuration.Revision, 1, "source-delivery-test", 1),
            TestData.UserId, "builder@example.test", new("registry.test", "tenant/application", "attempt-one",
                digest, "server-managed-pull"));
        var deploy = Services.GetRequiredService<DeploySourceBuildWebService>();
        var first = Assert.IsType<SourceBuildDeploymentResult>((await deploy(request)).Value);
        var active = await WebServices.Find(item => item.Id == service.Id).SingleAsync();
        var artifact = Assert.IsType<DeliveredSourceBuildImage>(active.Spec.Image);
        Assert.IsType<ActiveWebServiceLifecycle>(active.Lifecycle);
        Assert.Equal(digest, artifact.Digest);
        Assert.Equal(1, await Releases());
        Assert.IsType<DeployingSourceBuild>((await DbContext.SourceBuildJobs.Find(item => item.Id == job.Id)
            .SingleAsync()).State);
        Assert.IsNotType<PreparedResourceDeploymentState>((await DbContext.ResourceDeployments.Find(item =>
            item.ResourceId == service.Id).SingleAsync()).State);

        var events = await BusinessEvents();
        Assert.Equal(first, Assert.IsType<SourceBuildDeploymentResult>((await deploy(request)).Value));
        Assert.Equal(1, await Releases());
        Assert.Equal(events, await BusinessEvents());
        await WebServices.ReplaceOneAsync(item => item.Id == service.Id, active with
        {
            Revision = active.Revision + 1,
            Spec = active.Spec with
            {
                Config = active.Spec.Config with
                { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("DRIFT", "repair") }
            }
        });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await WebServices.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(artifact, repaired.Spec.Image);
        Assert.DoesNotContain("DRIFT", repaired.Spec.Config.EnvironmentVariables.Keys);
        Assert.Equal(2, await Releases());
        events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(repaired.Revision, (await WebServices.Find(item => item.Id == service.Id).SingleAsync()).Revision);
        Assert.Equal(2, await Releases());
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(1, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData("{\"sourceBuildConfiguration\":\"application-build\",\"tag\":\"fake\"}")]
    [InlineData("{\"sourceBuildConfiguration\":\"Invalid Name\"}")]
    [InlineData("{\"server\":\"registry.test\",\"name\":\"application\",\"tag\":\"1\",\"extra\":true}")]
    public async Task ImageAuthorityRejectsMixedOrInvalidDeclarationsBeforeMutation(string image)
    {
        var files = StageFiveWebSnapshot(spec => spec["image"] = JsonNode.Parse(image));
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(0, await WebServices.CountDocumentsAsync(_ => true));
    }
}
