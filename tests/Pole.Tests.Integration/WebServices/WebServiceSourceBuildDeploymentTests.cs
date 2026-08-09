using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceSourceBuildDeploymentTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task SourceBuildDeployment_CreatesTypedProvenanceAndCompletionEvent()
    {
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        await Fixture.Database.WebServices().InsertOneAsync(webService);
        var mutationWriter = new SuccessfulMutationWriter(webService);
        var service = new WebServiceSourceBuildDeploymentService(
            Services.GetRequiredService<LoadWebServiceById>(),
            Services.GetRequiredService<PersistUpdatedWebService>(),
            Services.GetRequiredService<PersistCurrentWebServiceRelease>(),
            Services.GetRequiredService<
                Pole.Database.RunMongoTransaction<SourceBuildDeploymentResult>>(),
            new SuccessfulComputeLookup(mutationWriter.Deployment),
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
        var updated = await Fixture.Database.WebServices().Find(item => item.Id == webService.Id).SingleAsync();
        Assert.Equal("harbor.test", updated.Spec.Image.Server);
        Assert.Equal("pole-registry-project-1", updated.Spec.Image.SecretName);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == deployment.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.SourceBuild, release.Action);
        var provenance = Assert.IsType<SourceBuildReleaseProvenance>(release.Provenance);
        Assert.Equal("build-1", provenance.BuildJobId);
        Assert.Equal(request.Image.Digest, provenance.ImageDigest);

        Assert.Equal(1, mutationWriter.ApplyCallCount);
        Assert.Equal(
            new ComputeProfileReference(
                mutationWriter.Deployment.ResolvedPlacement.Compute.ComputeProfileId,
                mutationWriter.Deployment.ResolvedPlacement.Compute.ComputeProfileRevision),
            mutationWriter.LastCommand?.ExpectedCompute);
        var applied = new ResourceDeploymentAppliedEvent(
            mutationWriter.Deployment.Id,
            WebServiceResourceDeployment.ResourceType.Value,
            updated.Id,
            mutationWriter.Deployment.RuntimeCellId,
            release.DeploymentGeneration)
        {
            EventCore = EventCore.Create(TestData.TenantId)
        };
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);
        await consumer.Handle(applied);

        var completedRelease = await Fixture.Database.WebServiceReleases()
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
                WebServiceResourceDeployment.ResourceType,
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
                    compute: ResourceDeploymentTestData.Compute(),
                    resolvedAtUtc: now),
                ResourcePlacementCapability.SingleTarget,
                runtimeCellReady: true,
                new ResourceDeploymentFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    now));
        }

        public ResourceDeployment Deployment { get; }
        public int ApplyCallCount { get; private set; }
        public ResourceDeploymentMutationRequest? LastCommand { get; private set; }

        public Task<Result<PlacementSummaryDto>> RequestApplyAsync(
            ResourceDeploymentMutationRequest command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            LastCommand = command;
            return Task.FromResult<Result<PlacementSummaryDto>>(
                Deployment.ToPlacementSummaryDto());
        }

        public Task<Result<PlacementSummaryDto>> RequestDeletionAsync(
            ResourceDeploymentMutationRequest command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<PlacementSummaryDto>>(
                Deployment.ToPlacementSummaryDto());
    }

    private sealed class SuccessfulComputeLookup(ResourceDeployment deployment)
        : IResourceDeploymentComputeLookup
    {
        public Task<Result<CurrentResourceDeploymentComputeDto>> GetCurrentAsync(
            string tenantId,
            ResourceTypeId resourceType,
            string resourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<CurrentResourceDeploymentComputeDto>>(
                new CurrentResourceDeploymentComputeDto(
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    new ComputeConfigurationSummaryDto(
                        new ComputeProfileReference(
                            deployment.ResolvedPlacement.Compute.ComputeProfileId,
                            deployment.ResolvedPlacement.Compute.ComputeProfileRevision),
                        deployment.ResolvedPlacement.Compute.DisplayName,
                        new ComputeResourceValuesDto(
                            deployment.ResolvedPlacement.Compute.Requests.CpuMillis,
                            deployment.ResolvedPlacement.Compute.Requests.MemoryMi),
                        new ComputeResourceValuesDto(
                            deployment.ResolvedPlacement.Compute.Limits.CpuMillis,
                            deployment.ResolvedPlacement.Compute.Limits.MemoryMi),
                        ImmutableList<ComputeExtendedResourceDto>.Empty)));
    }
}
