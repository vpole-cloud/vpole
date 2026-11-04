using Microsoft.Extensions.Configuration;
using Pole.Features.GitProviders.Application;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.GitProviders.Domain;
using Pole.Workloads;
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
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());

    private const string ConfigurationId = "507f1f77bcf86cd799439081";
    private const string JobId = "507f1f77bcf86cd799439082";
    private const string RepositoryId = "507f1f77bcf86cd799439083";
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
        Assert.Equal("harbor.test", updated.Spec.Image.ResolvedImage!.Server);
        Assert.Equal("pole-registry-project-1", updated.Spec.Image.ResolvedImage!.SecretName);
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == deployment.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.SourceBuild, release.Action);
        var provenance = Assert.IsType<SourceBuildReleaseProvenance>(release.Provenance);
        Assert.Equal(request.BuildJobId, provenance.BuildJobId);
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
            DesiredOperation = ResourceDeploymentDesiredOperation.Delete,
            Status = ResourceDeploymentStatus.Pending
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
        var allocated = TestData.SeedWebService(group.Id);
        allocated = allocated with
        {
            IsolationScopeId = group.DefaultIsolationScopeId,
            Spec = allocated.Spec with { Image = new UnboundSourceBuildImage() },
            Lifecycle = new PreparedWebServiceLifecycle(),
            Exposure = WebServiceExposure.Create(WebServiceExposureMode.Public)
        };
        var webService = allocated.AcceptConfigurationAwaitingArtifact(
            allocated.Spec with { Image = WebServiceImageFactory.Pending(ConfigurationId) },
            allocated.Tags, allocated.Exposure);
        var connection = GitProviderConnectionFactory.Create(TestData.TenantId, "build-github", GitProviderKind.GitHub,
            "installation", "account", "owner", GitProviderAccountType.User, GitProviderRepositorySelection.All,
            TestData.UserId, GitProviderConnectionFactory.DefaultOptions);
        await DbContext.GitProviderConnections.InsertOneAsync(connection);
        var now = DateTime.UtcNow;
        await DbContext.SourceRepositories.InsertOneAsync(SourceRepositoryFactory.Create(TestData.TenantId,
            "application", connection.Id, "application", "owner/application", "https://github.test/owner/application",
            true, "main", new(RepositoryId, now, now)));
        var configuration = SourceBuildConfigurationFactory.Create(TestData.TenantId, "application-build", RepositoryId,
            webService.Id, new("FROM scratch"), new ManualSourceBuildConfigurationOrigin(TestData.UserId),
            new(ConfigurationId, now)) with
        {
            BuildGeneration = 1
        };
        await DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        var job = SourceBuildJobFactory.CreateManual(TestData.TenantId, ConfigurationId, RepositoryId, webService.Id,
            "main", new string('a', 40), new(1, 1, 1), configuration.Recipe, TestData.UserId, TestData.UserEmail,
            new(JobId, now)) with
        {
            Execution = new ClaimedSourceBuildExecution("deployment-test", 1, now.AddMinutes(2))
        };
        job = job.StartCloning(now).StartAnalyzing(now).StartBuilding(now)
            .StartPushing("harbor.test/pole-test/web-services/service-1:build-1", Digest, now);
        await DbContext.SourceBuildJobs.InsertOneAsync(job);
        await DbContext.RunOutcomeInTransactionAsync<RegisteredResource>(
            async (transaction, cancellationToken) =>
            {
                var registration = await Services.GetRequiredService<IResourceRegistrar>()
                    .RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: webService.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: new ResourceRegistrationDescriptor(
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            webService.Name)),
                    session: transaction.Session,
                    cancellationToken: cancellationToken);
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
            Services.GetRequiredService<LoadWebServiceForSourceBuild>(),
            Services.GetRequiredService<Pole.Database.RunMongoOutcomeTransaction<DeploySourceBuildWebServiceOutcome>>(),
            new FixedSetQueryService(deployments).LoadAsync,
            requestSetApply,
            Services.GetRequiredService<PersistWebServiceArtifactPromotion>(),
            Services.GetRequiredService<BeginSourceBuildPromotion>(),
            Services.GetRequiredService<CompleteSourceBuildPromotion>(),
            Services.GetRequiredService<PersistCurrentWebServiceRelease>(),
            TimeProvider.System);

    private static SourceBuildDeploymentRequest CreateRequest(string webServiceId) =>
        new(
            TestData.TenantId,
            webServiceId,
            JobId,
            RepositoryId,
            new string('a', 40),
            new(ConfigurationId, 1, 1, "deployment-test", 1),
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildDeploymentImage(
                "harbor.test",
                "pole-test/web-services/service-1",
                "build-1",
                Digest,
                "pole-registry-project-1"));

    private static PlacementSummaryDto CreatePlacement(string deploymentId, string targetId) =>
        new(deploymentId, "507f1f77bcf86cd799439070", ObjectId.GenerateNewId().ToString(), targetId, DesiredGeneration: 1, ObservedGeneration: 1, Status: ResourceDeploymentStatus.Applied, FailureCode: null, new ComputeConfigurationSummaryDto(new ComputeProfileReference(ObjectId.GenerateNewId().ToString(), 1), "default", new ComputeResourceValuesDto(250, 256), new ComputeResourceValuesDto(1000, 512), ImmutableList<ComputeExtendedResourceDto>.Empty), Endpoints: [], ResourceRevision: 1, FixedReplicas: 2, Ingress: ResourceDeploymentIngressConfiguration.Create("traefik", "letsencrypt-prod", "example.test", [$"{deploymentId}.example.test"]), DeploymentRevision: 1, DesiredOperation: ResourceDeploymentDesiredOperation.Apply, Isolation: new ResourceDeploymentIsolationDto("Unknown", "EnforcementRequired", "EnforcementNotEstablished", null));

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
                        Status = ResourceDeploymentStatus.Pending
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
        public Task<LoadResourceDeploymentPlanningSnapshotOutcome> LoadAsync(
            ResourceDeploymentPlanningRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            Assert.True(session.IsInTransaction);
            var items = deployments.Select(item => new ResourceDeploymentPlanningItem(item.DeploymentId,
                "group", item.IsolationScopeId, item.DeploymentTargetId, item.DeploymentRevision, item.FixedReplicas,
                new(item.Compute.Profile.ComputeProfileId, item.Compute.Profile.ComputeProfileRevision,
                    item.DeploymentTargetId, item.Compute.DisplayName,
                    new(item.Compute.Requests.CpuMillis, item.Compute.Requests.MemoryMi),
                    new(item.Compute.Limits.CpuMillis, item.Compute.Limits.MemoryMi), ComputeArchitecture.Any,
                    [], [], [], [], [], null),
                item.DesiredOperation == ResourceDeploymentDesiredOperation.Delete
                    ? ResourceDeploymentPlanningState.Deleting : ResourceDeploymentPlanningState.Prepared,
                null, item.Ingress, 1)).ToArray();
            return Task.FromResult<LoadResourceDeploymentPlanningSnapshotOutcome>(
                new ResourceDeploymentPlanningSnapshot(request.TenantId, request.ResourceType, request.ResourceId,
                    items));
        }
    }
}
