// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class WebServiceSourceDeliveryTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task BuildOwnedWebServiceAcceptsConfigurationWithoutArtifactAndThenNoOps()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "demo-api")
            .SingleAsync();
        var configuration = await DbContext.SourceBuildConfigurations
            .Find(item => item.WebServiceId == service.Id)
            .SingleAsync();
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(service.Lifecycle);
        Assert.Equal(configuration.Id, Assert.IsType<PendingSourceBuildImage>(service.Spec.Image).ConfigurationId);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(placement.State);
        Assert.Equal(service.ActiveRevision, placement.ResourceRevision);
        Assert.Equal(0, await Releases());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));

        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var unchanged = await WebServices.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, unchanged.Revision);
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(unchanged.Lifecycle);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(0, await Releases());
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task ArtifactPromotionIsAtomicAndIdempotentAndGitOpsPreservesDeliveredImage()
    {
        await SeedApplicationConnectionAsync();
        var files = SourceDeliverySnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "demo-api")
            .SingleAsync();
        var configuration = await DbContext.SourceBuildConfigurations
            .Find(item => item.WebServiceId == service.Id)
            .SingleAsync();
        var application = await DbContext.SourceRepositories
            .Find(item => item.Id == configuration.SourceRepositoryId)
            .SingleAsync();
        await DbContext.SourceBuildConfigurations.UpdateOneAsync(
            item => item.Id == configuration.Id,
            Builders<SourceBuildConfiguration>.Update.Set(item => item.BuildGeneration, 1));
        var now = DateTime.UtcNow;
        var job = SourceBuildJobFactory.CreateManual(
                Data.TenantId,
                configuration.Id,
                application.Id,
                service.Id,
                application.Branch,
                new string('a', 40),
                new SourceBuildInputVersion(configuration.Revision, application.Revision, 1),
                configuration.Recipe,
                Data.UserId,
                "builder@example.test",
                SourceBuildJobFactory.DefaultOptions) with
            {
                Execution = new ClaimedSourceBuildExecution("source-delivery-test", 1, now.AddMinutes(2)),
            };
        var digest = "sha256:" + new string('d', 64);
        job = job.StartCloning(now)
            .StartAnalyzing(now)
            .StartBuilding(now)
            .StartPushing(
                SourceBuildArtifactFactory.Create("registry.test/tenant/application:attempt-one", digest),
                now);
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        var request = new SourceBuildDeploymentRequest(
            Data.TenantId,
            service.Id,
            job.Id,
            application.Id,
            job.CommitSha,
            new SourceBuildDeploymentAuthority(configuration.Id, configuration.Revision, 1, "source-delivery-test", 1),
            Data.UserId,
            "builder@example.test",
            new SourceBuildDeploymentImage(
                "registry.test",
                "tenant/application",
                "attempt-one",
                digest,
                "server-managed-pull"));
        var deploy = Services.GetRequiredService<DeploySourceBuildWebService>();
        var first = Assert.IsType<SourceBuildDeploymentResult>((await deploy(request)).Value);
        var active = await WebServices.Find(item => item.Id == service.Id).SingleAsync();
        var artifact = Assert.IsType<DeliveredSourceBuildImage>(active.Spec.Image);
        Assert.IsType<ActiveWebServiceLifecycle>(active.Lifecycle);
        Assert.Equal(digest, artifact.Digest);
        Assert.Equal(1, await Releases());
        Assert.IsType<DeployingSourceBuild>(
            (await DbContext.SourceBuildJobs.Find(item => item.Id == job.Id).SingleAsync()).State);
        Assert.IsNotType<PreparedResourceDeploymentState>(
            (await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync()).State);

        var events = await BusinessEvents();
        Assert.Equal(first, Assert.IsType<SourceBuildDeploymentResult>((await deploy(request)).Value));
        Assert.Equal(1, await Releases());
        Assert.Equal(events, await BusinessEvents());
        await WebServices.ReplaceOneAsync(
            item => item.Id == service.Id,
            active with
            {
                Revision = active.Revision + 1,
                Spec = active.Spec with
                {
                    Config = active.Spec.Config with
                    {
                        EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("DRIFT", "repair"),
                    },
                },
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
        Assert.Equal(1, await DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Theory]
    [InlineData("{\"sourceBuildConfiguration\":\"application-build\",\"tag\":\"fake\"}")]
    [InlineData("{\"sourceBuildConfiguration\":\"Invalid Name\"}")]
    [InlineData("{\"server\":\"registry.test\",\"name\":\"application\",\"tag\":\"1\",\"extra\":true}")]
    public async Task ImageAuthorityRejectsMixedOrInvalidDeclarationsBeforeMutation(string image)
    {
        var files = StageFiveWebSnapshot(spec => spec["image"] = JsonNode.Parse(image));
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(0, await WebServices.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }
}
