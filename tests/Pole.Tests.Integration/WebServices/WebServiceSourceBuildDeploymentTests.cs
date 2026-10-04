using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceSourceBuildDeploymentTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task SourceBuildDeployment_CreatesTypedProvenanceAndCompletionEvent()
    {
        var webService = await SeedWebServiceAsync();
        var deployments = new[]
        {
            CreatePlacement("507f1f77bcf86cd799439051", "507f1f77bcf86cd799439061"),
            CreatePlacement("507f1f77bcf86cd799439052", "507f1f77bcf86cd799439062")
        };
        var mutationWriter = new SuccessfulSetMutationWriter(deployments);
        var service = CreateService(deployments, mutationWriter.RequestSetApplyAsync);
        var request = CreateRequest(webService.Id);

        var result = await service.DeployAsync(request);

        var deployment = Assert.IsType<SourceBuildDeploymentResult>(result.Value);
        var updated = await Fixture.Database.WebServices().Find(item => item.Id == webService.Id).SingleAsync();
        Assert.Equal("harbor.test", updated.Spec.Image.Server);
        Assert.Equal("pole-registry-project-1", updated.Spec.Image.SecretName);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == deployment.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.SourceBuild, release.Action);
        var provenance = Assert.IsType<SourceBuildReleaseProvenance>(release.Provenance);
        Assert.Equal("build-1", provenance.BuildJobId);
        Assert.Equal(provenance.BuildJobId, release.ToDto().Provenance!.BuildJobId);
        Assert.Equal(provenance.SourceRepositoryId, release.ToDto().Provenance!.SourceRepositoryId);
        Assert.Equal(request.Image.Digest, provenance.ImageDigest);
        Assert.Equal(2, release.Deployments.Count);

        Assert.Equal(1, mutationWriter.ApplyCallCount);
        Assert.Equal(updated.ActiveRevision, mutationWriter.LastCommand?.ResourceRevision);
        Assert.Equal(deployments.Select(item => item.DeploymentId),
            mutationWriter.LastCommand?.Deployments.Select(item => item.ResourceDeploymentId));
        var releaseDeployment = release.Deployments[0];
        var applied = new ResourceDeploymentAppliedEvent(
            releaseDeployment.ResourceDeploymentId,
            WebServiceResourceDeployment.ResourceType.Value,
            updated.Id,
            "507f1f77bcf86cd799439071",
            releaseDeployment.DeploymentGeneration,
            releaseDeployment.ResourceRevision)
        {
            EventCore = EventCore.Create(TestData.TenantId)
        };
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);
        await consumer.Handle(applied);

        var partiallyCompletedRelease = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Pending, partiallyCompletedRelease.Outcome);
        Assert.Equal(WebServiceReleaseDeploymentOutcome.Succeeded,
            partiallyCompletedRelease.Deployments[0].Outcome);
        Assert.Equal(WebServiceReleaseDeploymentOutcome.Pending,
            partiallyCompletedRelease.Deployments[1].Outcome);

        var secondReleaseDeployment = release.Deployments[1];
        await consumer.Handle(applied with
        {
            ResourceDeploymentId = secondReleaseDeployment.ResourceDeploymentId,
            Generation = secondReleaseDeployment.DeploymentGeneration,
            ResourceRevision = secondReleaseDeployment.ResourceRevision
        });

        var completedRelease = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, completedRelease.Outcome);
        Assert.True(await Fixture.DbContext.OutboxMessages
            .Find(item => item.EventType == "pole.web-services/v1/web-service-deployment-completed")
            .AnyAsync());
    }

    [Fact]
    public async Task SourceBuildDeployment_AbortsOnTypedResourceDeploymentRejection()
    {
        var webService = await SeedWebServiceAsync();
        var deployment = CreatePlacement(
            "507f1f77bcf86cd799439051",
            "507f1f77bcf86cd799439061");
        var service = CreateService(
            [deployment],
            new RejectedSetMutationWriter(
                new ResourceDeploymentPlacementUnavailable(
                    webService.Id,
                    deployment.DeploymentId)).RequestSetApplyAsync);

        var result = await service.DeployAsync(CreateRequest(webService.Id));

        var error = Assert.IsType<SourceBuildWebServiceDeploymentError>(result.Value);
        var rejected = Assert.IsType<SourceBuildWebServiceDeploymentRejected>(error.Value);
        var unavailable = Assert.IsType<SourceBuildResourceDeploymentPlacementUnavailable>(
            rejected.Reason.Value);
        Assert.Equal(deployment.DeploymentId, unavailable.ResourceDeploymentId);

        var persisted = await Fixture.Database.WebServices()
            .Find(item => item.Id == webService.Id)
            .SingleAsync();
        Assert.Equal(webService.Spec.Image, persisted.Spec.Image);
        Assert.Equal(webService.ActiveRevision, persisted.ActiveRevision);
        Assert.False(await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == webService.Id)
            .AnyAsync());
        Assert.False(await Fixture.DbContext.OutboxMessages
            .Find(item => item.EventType == "pole.web-services/v1/web-service-updated")
            .AnyAsync());
    }

    [Fact]
    public async Task SourceBuildDeployment_DoesNotReapplyRemovedTarget()
    {
        var webService = await SeedWebServiceAsync();
        var desired = CreatePlacement("507f1f77bcf86cd799439051", "507f1f77bcf86cd799439061");
        var removed = CreatePlacement("507f1f77bcf86cd799439052", "507f1f77bcf86cd799439062") with
        {
            DesiredOperation = "Delete",
            Status = "Pending"
        };
        var writer = new SuccessfulSetMutationWriter([desired]);
        var service = CreateService([desired, removed], writer.RequestSetApplyAsync);

        var result = Assert.IsType<SourceBuildDeploymentResult>(
            (await service.DeployAsync(CreateRequest(webService.Id))).Value);

        Assert.Equal(desired.DeploymentId,
            Assert.Single(writer.LastCommand!.Deployments).ResourceDeploymentId);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == result.ReleaseId).SingleAsync();
        Assert.Equal(desired.DeploymentId,
            Assert.Single(release.Deployments).ResourceDeploymentId);
    }

    private async Task<WebService> SeedWebServiceAsync()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var webService = TestData.SeedWebService(group.Id);
        await DbContext.RunOutcomeInTransactionAsync<RegisteredResource>(
            async (transaction, cancellationToken) =>
            {
                var registration = await Services.GetRequiredService<IResourceRegistrar>()
                    .RegisterAsync(
                        webService.TenantId,
                        group.Id,
                        new ResourceRegistrationDescriptor(
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            webService.Name),
                        transaction.Session,
                        cancellationToken);
                var registered = Assert.IsType<RegisteredResource>(registration.Value);
                await Fixture.Database.WebServices().InsertOneAsync(
                    transaction.Session,
                    webService,
                    cancellationToken: cancellationToken);
                return transaction.Commit(registered);
            });
        return webService;
    }

    private WebServiceSourceBuildDeploymentService CreateService(
        IReadOnlyList<PlacementSummaryDto> deployments,
        RequestResourceDeploymentSetApply requestSetApply) =>
        new(
            Services.GetRequiredService<LoadWebServiceById>(),
            Services.GetRequiredService<PersistUpdatedWebServiceOutcome>(),
            Services.GetRequiredService<PersistCurrentWebServiceRelease>(),
            Services.GetRequiredService<
                Pole.Database.RunMongoOutcomeTransaction<
                    DeploySourceBuildWebServiceOutcome>>(),
            new FixedSetQueryService(deployments).LoadAsync,
            requestSetApply,
            TimeProvider.System);

    private static SourceBuildDeploymentRequest CreateRequest(string webServiceId) =>
        new(
            TestData.TenantId,
            webServiceId,
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

    private static PlacementSummaryDto CreatePlacement(string deploymentId, string targetId) =>
        new(
            deploymentId,
            "507f1f77bcf86cd799439070",
            ObjectId.GenerateNewId().ToString(),
            targetId,
            DesiredGeneration: 1,
            ObservedGeneration: 1,
            Status: "Applied",
            FailureCode: null,
            new ComputeConfigurationSummaryDto(
                new ComputeProfileReference(ObjectId.GenerateNewId().ToString(), 1),
                "default",
                new ComputeResourceValuesDto(250, 256),
                new ComputeResourceValuesDto(1000, 512),
                ImmutableList<ComputeExtendedResourceDto>.Empty),
            Endpoints: [],
            ResourceRevision: 1,
            FixedReplicas: 2,
            Ingress: ResourceDeploymentIngressConfiguration.Create(
                "traefik", "letsencrypt-prod", "example.test",
                [$"{deploymentId}.example.test"]),
            DeploymentRevision: 1);

    private sealed class SuccessfulSetMutationWriter(IReadOnlyList<PlacementSummaryDto> deployments)
    {
        public int ApplyCallCount { get; private set; }
        public ResourceDeploymentSetApplyRequest? LastCommand { get; private set; }

        public Task<ResourceDeploymentSetMutationOutcome> RequestSetApplyAsync(
            ResourceDeploymentSetApplyRequest command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            LastCommand = command;
            return Task.FromResult<ResourceDeploymentSetMutationOutcome>(
                new MutatedResourceDeployments(
                    deployments.Select(item => item with
                    {
                        DesiredGeneration = item.DesiredGeneration + 1,
                        ResourceRevision = command.ResourceRevision,
                        Status = "Pending"
                    }).ToArray()));
        }
    }

    private sealed class RejectedSetMutationWriter(ResourceDeploymentMutationError error)
    {
        public Task<ResourceDeploymentSetMutationOutcome> RequestSetApplyAsync(
            ResourceDeploymentSetApplyRequest command,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentSetMutationOutcome>(error);
    }

    private sealed class FixedSetQueryService(IReadOnlyList<PlacementSummaryDto> deployments)
    {
        public Task<IReadOnlyList<PlacementSummaryDto>> LoadAsync(
            string tenantId,
            ResourceTypeId resourceType,
            string resourceId,
            CancellationToken cancellationToken = default) => Task.FromResult(deployments);
    }
}
