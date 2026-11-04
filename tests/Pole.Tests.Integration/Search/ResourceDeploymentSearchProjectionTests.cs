using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Consumers;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Search.Contracts;
using Pole.Features.Search.Domain;
using Pole.Features.WebServices.Application.Search;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Search;

public sealed class ResourceDeploymentSearchProjectionTests(
    MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private CreateSearchProjection CreateProjection =>
        Services.GetRequiredService<CreateSearchProjection>();
    private UpdateSearchOperationalStatus UpdateStatus =>
        Services.GetRequiredService<UpdateSearchOperationalStatus>();
    private ResolveSearchSourceType ResolveSourceType =>
        Services.GetRequiredService<ResolveSearchSourceType>();
    private IMongoCollection<SearchSourceDocument> SearchSources =>
        Fixture.Database.GetCollection<SearchSourceDocument>("SearchSourceDocuments");

    [Fact]
    public async Task OperationalStatusBeforeLogicalSnapshot_IsPreservedByLaterCreate()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var consumer = CreateConsumer(CreatePlacement(
            deploymentId,
            desiredGeneration: 2,
            observedGeneration: 2,
            status: ResourceDeploymentStatus.Applied));

        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceSearchProjection.SourceType.ResourceTypeId.Value,
            resourceId,
            2,
            DateTime.UtcNow,
            false,
            CancellationToken.None);

        await CreateProjection(CreateWebSnapshot(tenantId, resourceId, "eventually-created"));

        var projected = await LoadAsync(tenantId, resourceId);
        Assert.Equal("eventually-created", projected.Title);
        Assert.Contains(projected.SearchKeys, key => key.Value == "eventually-created");
        Assert.NotNull(projected.OperationalStatus);
        Assert.Equal("Applied", projected.OperationalStatus.Value.Status);
        Assert.Equal(2, projected.OperationalStatus.Value.ResourceRevision);
        Assert.Equal(2, projected.OperationalStatus.Value.ObservedGeneration);
    }

    [Fact]
    public async Task OlderStatusCannotOverwriteNewerStatus_AndIdentityIsTenantScoped()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        await CreateProjection(CreateWebSnapshot(tenantId, resourceId, "worker"));
        await CreateProjection(CreateWebSnapshot(otherTenantId, resourceId, "other-worker"));

        var stub = new StubResourceDeploymentQueryService([
            CreatePlacement(deploymentId, 3, 3, ResourceDeploymentStatus.Applied)
        ]);
        var consumer = new ResourceDeploymentSearchProjectionConsumer(
            UpdateStatus,
            stub.LoadAsync,
            ResolveSourceType);
        var currentEventTime = DateTime.UtcNow;

        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceSearchProjection.SourceType.ResourceTypeId.Value,
            resourceId,
            3,
            currentEventTime,
            false,
            CancellationToken.None);

        stub.Current = [CreatePlacement(
            deploymentId,
            desiredGeneration: 2,
            observedGeneration: 1,
            status: ResourceDeploymentStatus.Failed,
            failureCode: "ProviderUnavailable")];
        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceSearchProjection.SourceType.ResourceTypeId.Value,
            resourceId,
            2,
            currentEventTime.AddMinutes(-1),
            false,
            CancellationToken.None);

        var projected = await LoadAsync(tenantId, resourceId);
        Assert.Equal("Applied", projected.OperationalStatus.Value.Status);
        Assert.Equal(3, projected.OperationalStatus.Value.ResourceRevision);

        var otherTenantProjection = await LoadAsync(otherTenantId, resourceId);
        Assert.Null(otherTenantProjection.OperationalStatus);
        Assert.Equal("other-worker", otherTenantProjection.Title);
    }

    [Fact]
    public async Task MultiTargetStatus_IsAggregatedAndSanitized()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var appliedId = ObjectId.GenerateNewId().ToString();
        var failedId = ObjectId.GenerateNewId().ToString();
        await CreateProjection(CreateWebSnapshot(tenantId, resourceId, "multi-target"));
        var consumer = CreateConsumer(
            CreatePlacement(appliedId, 4, 4, ResourceDeploymentStatus.Applied),
            CreatePlacement(failedId, 5, 3, ResourceDeploymentStatus.Failed, "ProviderUnavailable"));

        await consumer.Handle(
            tenantId,
            failedId,
            WebServiceSearchProjection.SourceType.ResourceTypeId.Value,
            resourceId,
            3,
            DateTime.UtcNow,
            false,
            CancellationToken.None);

        var projected = await LoadAsync(tenantId, resourceId);
        Assert.Equal("PartiallyFailed", projected.OperationalStatus.Value.Status);
        Assert.Equal(5, projected.OperationalStatus.Value.ResourceRevision);
        Assert.Equal(3, projected.OperationalStatus.Value.ObservedGeneration);
        Assert.Equal("ProviderUnavailable", projected.OperationalStatus.Value.FailureCode.Value);
        var status = projected.OperationalStatus.Value.ToBsonDocument();
        Assert.DoesNotContain("DeploymentId", status.Names);
        Assert.DoesNotContain("RuntimeCellId", status.Names);
        Assert.DoesNotContain("Endpoints", status.Names);
    }

    [Fact]
    public async Task UnregisteredResourceType_IsIgnoredWithoutLoadingDeployments()
    {
        var stub = new StubResourceDeploymentQueryService([]);
        var consumer = new ResourceDeploymentSearchProjectionConsumer(
            UpdateStatus,
            stub.LoadAsync,
            ResolveSourceType);

        await consumer.Handle(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            "third-party/not-registered",
            ObjectId.GenerateNewId().ToString(),
            1,
            DateTime.UtcNow,
            false,
            CancellationToken.None);

        Assert.Equal(0, stub.LoadCount);
        Assert.Equal(0, await SearchSources.CountDocumentsAsync(FilterDefinition<SearchSourceDocument>.Empty));
    }

    private ResourceDeploymentSearchProjectionConsumer CreateConsumer(
        params PlacementSummaryDto[] placements) =>
        new(
            UpdateStatus,
            new StubResourceDeploymentQueryService(placements).LoadAsync,
            ResolveSourceType);

    private Task<SearchSourceDocument> LoadAsync(string tenantId, string resourceId) =>
        SearchSources.Find(source =>
            source.TenantId == tenantId &&
            source.ResourceTypeId == WebServiceSearchProjection.SourceType.ResourceTypeId.Value &&
            source.SourceId == resourceId).SingleAsync();

    private static SearchProjectionSnapshot CreateWebSnapshot(
        string tenantId,
        string resourceId,
        string title) =>
        new(
            new SearchProjectionIdentity(
                tenantId,
                WebServiceSearchProjection.SourceType.ResourceTypeId,
                resourceId),
            title,
            ObjectId.GenerateNewId().ToString(),
            null,
            [
                new SearchProjectionField(SearchProjectionFieldType.SourceId, resourceId),
                new SearchProjectionField(SearchProjectionFieldType.Name, title)
            ],
            1);

    private static PlacementSummaryDto CreatePlacement(
        string deploymentId,
        long desiredGeneration,
        long observedGeneration,
        ResourceDeploymentStatus status,
        string? failureCode = null) =>
        new(deploymentId, ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), TestData.DefaultDeploymentTargetId, desiredGeneration, observedGeneration, status, failureCode, new ComputeConfigurationSummaryDto(new ComputeProfileReference(TestData.DefaultComputeProfileId, 1), "General purpose", new ComputeResourceValuesDto(250, 256), new ComputeResourceValuesDto(1000, 1024), []), ImmutableList<ResourceDeploymentEndpointDto>.Empty, ResourceRevision: desiredGeneration, FixedReplicas: 1, Ingress: null, DeploymentRevision: 1, DesiredOperation: ResourceDeploymentDesiredOperation.Apply, Isolation: new ResourceDeploymentIsolationDto("Unknown", "EnforcementRequired", "EnforcementNotEstablished", null));

    private sealed class StubResourceDeploymentQueryService(
        IReadOnlyList<PlacementSummaryDto> current)
    {
        public IReadOnlyList<PlacementSummaryDto> Current { get; set; } = current;
        public int LoadCount { get; private set; }

        public Task<IReadOnlyList<PlacementSummaryDto>> LoadAsync(
            LoadResourceDeploymentSetRequest request,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(Current);
        }
    }
}
