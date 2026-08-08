using System.Collections.Immutable;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Consumers;
using Pole.Features.Search.Application.Services;
using Pole.Features.Search.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Search;

public sealed class ResourceDeploymentSearchProjectionTests(
    MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string WebServiceResourceType = "pole/web-service";

    private ISearchService SearchService =>
        Services.GetRequiredService<ISearchService>();

    private IMongoCollection<SearchSourceDocument> SourceDocuments =>
        Fixture.Database.GetCollection<SearchSourceDocument>(
            "SearchSourceDocuments");

    [Fact]
    public async Task AppliedProjection_PreservesLogicalMetadata_AndReturnsOperationalStatus()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var placement = CreatePlacement(
            deploymentId,
            desiredGeneration: 2,
            observedGeneration: 2,
            status: "Applied",
            failureCode: null);
        await InsertSourceAsync(tenantId, resourceId, "portal");

        var consumer = CreateConsumer(placement);
        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 2,
            DateTime.UtcNow,
            CancellationToken.None);

        var projected = await SourceDocuments
            .Find(document =>
                document.TenantId == tenantId &&
                document.SourceId == resourceId)
            .SingleAsync();

        Assert.Equal("portal", projected.Title);
        Assert.Contains(projected.SearchKeys, key => key.Value == "portal");
        Assert.NotNull(projected.OperationalStatus);
        Assert.Equal("Applied", projected.OperationalStatus.Value.Status);
        Assert.Equal(2, projected.OperationalStatus.Value.DesiredGeneration);
        Assert.Equal(2, projected.OperationalStatus.Value.ObservedGeneration);
        Assert.Null(projected.OperationalStatus.Value.FailureCode);

        var persistedStatus = projected.OperationalStatus.Value.ToBsonDocument();
        Assert.DoesNotContain("DeploymentId", persistedStatus.Names);
        Assert.DoesNotContain("DeploymentTargetId", persistedStatus.Names);
        Assert.DoesNotContain("IsolationScopeId", persistedStatus.Names);
        Assert.DoesNotContain("RuntimeCellId", persistedStatus.Names);
        Assert.DoesNotContain("Endpoints", persistedStatus.Names);

        var result = await SearchService.Get(
            SearchQueryModule.FromRequest(new GetSearchRequest
            {
                TenantId = tenantId,
                Query = "port",
                PageNumber = 1,
                PageSize = 10
            }),
            CancellationToken.None);

        var hit = Assert.Single(result.Items);
        Assert.NotNull(hit.OperationalStatus);
        Assert.Equal("Applied", hit.OperationalStatus.Value.Status);
        Assert.Null(hit.OperationalStatus.Value.FailureCode);
    }

    [Fact]
    public async Task Projection_IsTenantScoped_AndDoesNotAllowOlderGenerationToOverwrite()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        await InsertSourceAsync(tenantId, resourceId, "worker");
        await InsertSourceAsync(otherTenantId, resourceId, "other-worker");

        var current = CreatePlacement(
            deploymentId,
            desiredGeneration: 3,
            observedGeneration: 3,
            status: "Applied",
            failureCode: null);
        var stub = new StubResourceDeploymentQueryService(current);
        var consumer = new ResourceDeploymentSearchProjectionConsumer(
            SearchService,
            stub);
        var currentEventTime = DateTime.UtcNow;

        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 3,
            currentEventTime,
            CancellationToken.None);

        stub.Current = CreatePlacement(
            deploymentId,
            desiredGeneration: 2,
            observedGeneration: 1,
            status: "Failed",
            failureCode: "ProviderUnavailable");
        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 2,
            currentEventTime.AddMinutes(-1),
            CancellationToken.None);

        await consumer.Handle(
            otherTenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 2,
            currentEventTime.AddMinutes(1),
            CancellationToken.None);

        var projected = await SourceDocuments
            .Find(document =>
                document.TenantId == tenantId &&
                document.SourceId == resourceId)
            .SingleAsync();

        Assert.NotNull(projected.OperationalStatus);
        Assert.Equal(3, projected.OperationalStatus.Value.DesiredGeneration);
        Assert.Equal(3, projected.OperationalStatus.Value.ObservedGeneration);
        Assert.Equal("Applied", projected.OperationalStatus.Value.Status);
        Assert.Null(projected.OperationalStatus.Value.FailureCode);

        var otherTenantProjection = await SourceDocuments
            .Find(document =>
                document.TenantId == otherTenantId &&
                document.SourceId == resourceId)
            .SingleAsync();
        Assert.NotNull(otherTenantProjection.OperationalStatus);
        Assert.Equal("Failed", otherTenantProjection.OperationalStatus.Value.Status);
        Assert.Equal(
            "ProviderUnavailable",
            otherTenantProjection.OperationalStatus.Value.FailureCode);
    }

    [Theory]
    [InlineData("Failed", "ProviderUnavailable")]
    [InlineData("Deleted", null)]
    public async Task TerminalProjection_ExposesOnlySanitizedStatusDetails(
        string status,
        string? failureCode)
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        await InsertSourceAsync(tenantId, resourceId, "terminal-service");

        var consumer = CreateConsumer(CreatePlacement(
            deploymentId,
            desiredGeneration: 4,
            observedGeneration: status == "Deleted" ? 4 : 3,
            status,
            failureCode));
        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 4,
            DateTime.UtcNow,
            CancellationToken.None);

        var projected = await SourceDocuments
            .Find(document =>
                document.TenantId == tenantId &&
                document.SourceId == resourceId)
            .SingleAsync();

        Assert.NotNull(projected.OperationalStatus);
        Assert.Equal(status, projected.OperationalStatus.Value.Status);
        if (failureCode is null)
        {
            Assert.Null(projected.OperationalStatus.Value.FailureCode);
        }
        else
        {
            Assert.NotNull(projected.OperationalStatus.Value.FailureCode);
            Assert.Equal(
                failureCode,
                projected.OperationalStatus.Value.FailureCode.Value);
        }
        Assert.DoesNotContain(
            projected.ToBsonDocument().Names,
            name => name.Contains("Namespace", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LogicalProjectionRebuild_PreservesExistingOperationalStatus()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var resourceId = ObjectId.GenerateNewId().ToString();
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var deploymentId = ObjectId.GenerateNewId().ToString();
        await InsertSourceAsync(tenantId, resourceId, "old-name");

        var consumer = CreateConsumer(CreatePlacement(
            deploymentId,
            desiredGeneration: 1,
            observedGeneration: 1,
            status: "Applied",
            failureCode: null));
        await consumer.Handle(
            tenantId,
            deploymentId,
            WebServiceResourceType,
            resourceId,
            generation: 1,
            DateTime.UtcNow,
            CancellationToken.None);

        var replacementRow = new SearchDocument(
            ObjectId.GenerateNewId().ToString(),
            SearchDocumentType.WebService,
            tenantId,
            resourceId,
            SearchKeyType.Name,
            "new-name",
            100_000,
            "new-name",
            FSharpOption<string>.Some(resourceGroupId),
            FSharpOption<string>.None);
        var replacement = new SearchDocumentSet(
            SearchDocumentType.WebService,
            tenantId,
            resourceId,
            ListModule.OfSeq([replacementRow]));

        await SearchService.Update(replacement, CancellationToken.None);

        var projected = await SourceDocuments
            .Find(document =>
                document.TenantId == tenantId &&
                document.SourceId == resourceId)
            .SingleAsync();

        Assert.Equal("new-name", projected.Title);
        Assert.NotNull(projected.OperationalStatus);
        Assert.Equal("Applied", projected.OperationalStatus.Value.Status);
    }

    private ResourceDeploymentSearchProjectionConsumer CreateConsumer(
        PlacementSummaryDto placement) =>
        new(
            SearchService,
            new StubResourceDeploymentQueryService(placement));

    private async Task InsertSourceAsync(
        string tenantId,
        string sourceId,
        string title)
    {
        var raw = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["SourceType"] = (int)SearchDocumentType.WebService,
            ["TenantId"] = tenantId,
            ["SourceId"] = sourceId,
            ["Title"] = title,
            ["ResourceGroupId"] = BsonNull.Value,
            ["ResourceGroupName"] = BsonNull.Value,
            ["SearchKeys"] = new BsonArray
            {
                new BsonDocument
                {
                    ["Value"] = title,
                    ["Weight"] = 100_000
                }
            }
        };

        await Fixture.Database
            .GetCollection<BsonDocument>("SearchSourceDocuments")
            .InsertOneAsync(raw);
    }

    private static PlacementSummaryDto CreatePlacement(
        string deploymentId,
        long desiredGeneration,
        long observedGeneration,
        string status,
        string? failureCode) =>
        new(
            deploymentId,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            desiredGeneration,
            observedGeneration,
            status,
            failureCode,
            new ComputeConfigurationSummaryDto(
                new ComputeProfileReference(
                    ObjectId.GenerateNewId().ToString(),
                    ComputeProfileRevision: 1),
                "General purpose",
                new ComputeResourceValuesDto(250, 256),
                new ComputeResourceValuesDto(1000, 1024),
                []),
            ImmutableList.Create(
                new ResourceDeploymentEndpointDto(
                    "Ingress",
                    "portal.example.com",
                    443,
                    "https")));

    private sealed class StubResourceDeploymentQueryService(
        PlacementSummaryDto current)
        : IResourceDeploymentQueryService
    {
        public PlacementSummaryDto Current { get; set; } = current;

        public Task<PlacementSummaryDto?> GetAsync(
            string tenantId,
            ResourceTypeId resourceType,
            string resourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PlacementSummaryDto?>(Current);

        public Task<IReadOnlyDictionary<string, PlacementSummaryDto>> GetManyAsync(
            string tenantId,
            ResourceTypeId resourceType,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PlacementSummaryDto>>(
                resourceIds.ToDictionary(
                    resourceId => resourceId,
                    _ => Current,
                    StringComparer.Ordinal));
    }
}
