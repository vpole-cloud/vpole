using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceSourceBuildDeploymentTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task SourceBuildDeployment_CreatesTypedProvenanceAndCompletionEvent()
    {
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        await Fixture.DbContext.WebServices.InsertOneAsync(webService);
        var mutationWriter = new SuccessfulMutationWriter(webService);
        var service = new WebServiceSourceBuildDeploymentService(
            Fixture.DbContext,
            mutationWriter);
        var request = new SourceBuildDeploymentRequest(
            TestData.TenantId,
            webService.Id,
            "build-1",
            "source-1",
            "abc123",
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildDeploymentImage(
                "harbor.test",
                "pole-test/web-services/service-1",
                "build-1",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "pole-registry-project-1"));

        var result = await service.DeployAsync(request);

        var deployment = result.AssertSuccess();
        var updated = await Fixture.DbContext.WebServices.Find(item => item.Id == webService.Id).SingleAsync();
        Assert.Equal("harbor.test", updated.Spec.Image.Server);
        Assert.Equal("pole-registry-project-1", updated.Spec.Image.SecretName);
        var release = await Fixture.DbContext.WebServiceReleases
            .Find(item => item.Id == deployment.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.SourceBuild, release.Action);
        var provenance = Assert.IsType<SourceBuildReleaseProvenance>(release.Provenance);
        Assert.Equal("build-1", provenance.BuildJobId);
        Assert.Equal(request.Image.Digest, provenance.ImageDigest);

        Assert.Equal(1, mutationWriter.ApplyCallCount);
        var applied = new ResourceDeploymentAppliedEvent(
            mutationWriter.Deployment.Id,
            ResourceDeploymentResourceTypes.WebService.Value,
            updated.Id,
            mutationWriter.Deployment.RuntimeCellId,
            release.DeploymentGeneration)
        {
            EventCore = EventCore.Create(TestData.TenantId)
        };
        var consumer = new WebServiceDeploymentConsumer(
            Fixture.DbContext,
            TimeProvider.System);
        await consumer.Handle(applied);

        var completedRelease = await Fixture.DbContext.WebServiceReleases
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, completedRelease.Outcome);
        Assert.True(await Fixture.DbContext.OutboxMessages
            .Find(item => item.EventType == "web-service-deployment-completed")
            .AnyAsync());
    }

    private sealed class SuccessfulMutationWriter
        : IResourceDeploymentMutationWriter
    {
        public SuccessfulMutationWriter(WebService webService)
        {
            var now = DateTime.UtcNow;
            var runtimeCellId = ObjectId.GenerateNewId().ToString();
            Deployment = ResourceDeploymentFactory.Create(
                webService.TenantId,
                ResourceDeploymentResourceTypes.WebService,
                webService.Id,
                webService.ResourceGroupId,
                ObjectId.GenerateNewId().ToString(),
                ResolvedResourcePlacement.Create(
                    runtimeCellId,
                    TestData.DefaultDeploymentTargetId,
                    "test-apps",
                    runtimeCellGeneration: 1,
                    targetWorkloadConfigurationRevision: 1,
                    ResolvedIngressConfiguration.Create(
                        "traefik",
                        "letsencrypt-prod"),
                    storage: null,
                    now),
                ResourcePlacementCapability.SingleTarget,
                runtimeCellReady: true,
                ResourceDeploymentFactory.DefaultOptions with
                {
                    CreatedAtUtc = now
                });
        }

        public ResourceDeployment Deployment { get; }
        public int ApplyCallCount { get; private set; }

        public Task<Result<ResourceDeployment>> RequestApplyAsync(
            ResourceDeploymentMutationCommand command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            return Task.FromResult<Result<ResourceDeployment>>(Deployment);
        }

        public Task<Result<ResourceDeployment>> RequestDeletionAsync(
            ResourceDeploymentMutationCommand command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<ResourceDeployment>>(Deployment);
    }
}
