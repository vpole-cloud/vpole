using Pole.Migrations;
using System.Collections.Immutable;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Search;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Migrations;
using Pole.Features.Search.Application.Projection;
using Pole.Features.Search.Contracts;
using Pole.Features.Search.Domain;
using Pole.Features.WebServices.Application.Search;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Search;

public sealed class SearchIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private CreateSearchProjection CreateProjection =>
        Services.GetRequiredService<CreateSearchProjection>();
    private UpdateSearchProjection UpdateProjection =>
        Services.GetRequiredService<UpdateSearchProjection>();
    private DeleteSearchProjection DeleteProjection =>
        Services.GetRequiredService<DeleteSearchProjection>();
    private UpdateSearchOperationalStatus UpdateStatus =>
        Services.GetRequiredService<UpdateSearchOperationalStatus>();
    private IMongoCollection<SearchDocument> SearchDocuments =>
        Fixture.Database.GetCollection<SearchDocument>("SearchDocuments");
    private IMongoCollection<SearchSourceDocument> SearchSources =>
        Fixture.Database.GetCollection<SearchSourceDocument>("SearchSourceDocuments");
    private IMongoCollection<SearchProjectionVersionDocument> SearchProjectionVersions =>
        Fixture.Database.GetCollection<SearchProjectionVersionDocument>("SearchProjectionVersions");

    [Fact]
    public async Task ProviderOwnedConsumers_ProjectAllRegisteredSourceTypes()
    {
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var webServiceId = ObjectId.GenerateNewId().ToString();

        await new AddResourceGroupToSearchOnResourceGroupCreatedConsumer(CreateProjection)
            .Handle(new ResourceGroupCreatedEvent(
                resourceGroupId,
                "platform",
                "description",
                ImmutableHashSet.Create("production"),
                1)
            {
                EventCore = EventCore.Create(TestData.TenantId)
            }, CancellationToken.None);

        await new AddWebServiceToSearchOnWebServiceCreatedConsumer(CreateProjection)
            .Handle(CreateWebServiceEvent(webServiceId, resourceGroupId, "portal"),
                CancellationToken.None);

        var sources = await SearchSources.Find(source =>
            source.TenantId == TestData.TenantId).ToListAsync();

        Assert.Equal(2, sources.Count);
        Assert.Equal(
            ["ResourceGroup", "WebService"],
            sources.OrderBy(source => source.SourceSortOrder)
                .Select(source => source.SourceApiName));
        Assert.All(sources, source => Assert.False(string.IsNullOrWhiteSpace(source.ResourceTypeId)));
    }

    [Fact]
    public async Task CreateUpdateAndRepeatedDelete_MutateBothCollectionsIdempotently()
    {
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var webServiceId = ObjectId.GenerateNewId().ToString();
        var createConsumer = new AddWebServiceToSearchOnWebServiceCreatedConsumer(CreateProjection);
        var updateConsumer = new UpdateWebServiceInSearchOnWebServiceUpdatedConsumer(UpdateProjection);
        var deleteConsumer = new RemoveWebServiceFromSearchOnWebServiceDeletedConsumer(DeleteProjection);

        await createConsumer.Handle(
            CreateWebServiceEvent(webServiceId, resourceGroupId, "web-api"),
            CancellationToken.None);

        var rows = await SearchDocuments.Find(document =>
            document.TenantId == TestData.TenantId &&
            document.ResourceTypeId == "pole/web-service" &&
            document.SourceId == webServiceId).ToListAsync();
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name &&
            row.SearchValue == "web-api" && row.Weight == SearchDocumentFactory.NameWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name &&
            row.SearchValue == "api" && row.Weight == SearchDocumentFactory.NameTokenWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Domain &&
            row.SearchValue == "portal.example.test" && row.Weight == SearchDocumentFactory.DomainWeight);

        await updateConsumer.Handle(
            CreateWebServiceUpdatedEvent(webServiceId, resourceGroupId, "renamed-service"),
            CancellationToken.None);

        var source = await SearchSources.Find(document =>
            document.TenantId == TestData.TenantId &&
            document.ResourceTypeId == "pole/web-service" &&
            document.SourceId == webServiceId).SingleAsync();
        Assert.Equal("renamed-service", source.Title);
        Assert.Contains(source.SearchKeys, key => key.Value == "renamed-service");
        Assert.DoesNotContain(source.SearchKeys, key => key.Value == "web-api");

        await UpdateProjection(new SearchProjectionSnapshot(
            new SearchProjectionIdentity(
                TestData.TenantId,
                WebServiceSearchProjection.SourceType.ResourceTypeId,
                webServiceId),
            "metadata-only",
            resourceGroupId,
            null,
            [],
            3));
        source = await SearchSources.Find(document =>
            document.TenantId == TestData.TenantId &&
            document.ResourceTypeId == "pole/web-service" &&
            document.SourceId == webServiceId).SingleAsync();
        Assert.Equal("metadata-only", source.Title);
        Assert.Empty(source.SearchKeys);
        Assert.Equal(0, await SearchDocuments.CountDocumentsAsync(document =>
            document.TenantId == TestData.TenantId && document.SourceId == webServiceId));

        await deleteConsumer.Handle(TestData.TenantId, webServiceId, 4, CancellationToken.None);
        await deleteConsumer.Handle(TestData.TenantId, webServiceId, 4, CancellationToken.None);

        Assert.Equal(0, await SearchDocuments.CountDocumentsAsync(document =>
            document.TenantId == TestData.TenantId && document.SourceId == webServiceId));
        Assert.Equal(0, await SearchSources.CountDocumentsAsync(document =>
            document.TenantId == TestData.TenantId && document.SourceId == webServiceId));
        var tombstone = await SearchProjectionVersions.Find(version =>
            version.TenantId == TestData.TenantId &&
            version.SourceId == webServiceId).SingleAsync();
        Assert.Equal(4, tombstone.ResourceRevision);
        Assert.True(tombstone.IsDeleted);
    }

    [Fact]
    public async Task OutOfOrderLifecycleEvents_CannotOverwriteOrResurrectNewerProjection()
    {
        var sourceId = ObjectId.GenerateNewId().ToString();
        var identity = new SearchProjectionIdentity(
            TestData.TenantId,
            WebServiceSearchProjection.SourceType.ResourceTypeId,
            sourceId);

        await CreateProjection(CreateSnapshot(
            TestData.TenantId,
            identity.ResourceTypeId,
            sourceId,
            "created",
            1));
        await UpdateProjection(CreateSnapshot(
            TestData.TenantId,
            identity.ResourceTypeId,
            sourceId,
            "newest",
            3));
        await UpdateProjection(CreateSnapshot(
            TestData.TenantId,
            identity.ResourceTypeId,
            sourceId,
            "stale-update",
            2));

        var projected = await SearchSources.Find(source =>
            source.TenantId == TestData.TenantId &&
            source.ResourceTypeId == identity.ResourceTypeId.Value &&
            source.SourceId == sourceId).SingleAsync();
        Assert.Equal("newest", projected.Title);

        await DeleteProjection(identity, 5);
        await UpdateProjection(CreateSnapshot(
            TestData.TenantId,
            identity.ResourceTypeId,
            sourceId,
            "late-after-delete",
            4));
        await UpdateStatus(
            identity,
            new SearchOperationalStatusSnapshot(
                4,
                4,
                "Applied",
                null,
                DateTime.UtcNow));

        Assert.Equal(0, await SearchDocuments.CountDocumentsAsync(document =>
            document.TenantId == TestData.TenantId && document.SourceId == sourceId));
        Assert.Equal(0, await SearchSources.CountDocumentsAsync(source =>
            source.TenantId == TestData.TenantId && source.SourceId == sourceId));
        var tombstone = await SearchProjectionVersions.Find(version =>
            version.TenantId == TestData.TenantId && version.SourceId == sourceId).SingleAsync();
        Assert.Equal(5, tombstone.ResourceRevision);
        Assert.True(tombstone.IsDeleted);
    }

    [Fact]
    public async Task ConcurrentLifecycleEvents_ConvergeOnHighestResourceRevision()
    {
        var sourceId = ObjectId.GenerateNewId().ToString();
        var resourceTypeId = WebServiceSearchProjection.SourceType.ResourceTypeId;

        await Task.WhenAll(Enumerable.Range(1, 8).Select(resourceRevision =>
            UpdateProjection(CreateSnapshot(
                TestData.TenantId,
                resourceTypeId,
                sourceId,
                $"revision-{resourceRevision}",
                resourceRevision))));

        var projected = await SearchSources.Find(source =>
            source.TenantId == TestData.TenantId &&
            source.ResourceTypeId == resourceTypeId.Value &&
            source.SourceId == sourceId).SingleAsync();
        var version = await SearchProjectionVersions.Find(candidate =>
            candidate.TenantId == TestData.TenantId &&
            candidate.ResourceTypeId == resourceTypeId.Value &&
            candidate.SourceId == sourceId).SingleAsync();

        Assert.Equal("revision-8", projected.Title);
        Assert.Equal(8, version.ResourceRevision);
        Assert.False(version.IsDeleted);
    }

    [Fact]
    public async Task SameResourceIdentityAcrossTenants_RemainsIsolated()
    {
        var sourceId = ObjectId.GenerateNewId().ToString();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        await CreateProjection(CreateWidget(TestData.TenantId, sourceId, "shared-widget"));
        await CreateProjection(CreateWidget(otherTenantId, sourceId, "shared-widget"));

        var response = await SearchAsync(TestData.TenantId, "shared");

        var hit = Assert.Single(response.Items);
        Assert.Equal(sourceId, hit.SourceId);
        Assert.Equal("Widget", hit.SourceType);
        Assert.Equal("third-party/widget", hit.ResourceTypeId);
        Assert.Equal(2, await SearchSources.CountDocumentsAsync(source => source.SourceId == sourceId));
    }

    [Fact]
    public async Task SyntheticSourceType_WorksWithoutSearchCoreChanges_AndKeepsOrdering()
    {
        var sharedTitle = "same-title";
        await CreateProjection(CreateSnapshot(
            TestData.TenantId,
            ResourceGroupSearchProjection.SourceType.ResourceTypeId,
            ObjectId.GenerateNewId().ToString(),
            sharedTitle));
        await CreateProjection(CreateWidget(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            sharedTitle));
        await CreateProjection(CreateSnapshot(
            TestData.TenantId,
            WebServiceSearchProjection.SourceType.ResourceTypeId,
            ObjectId.GenerateNewId().ToString(),
            sharedTitle));

        var response = await SearchAsync(TestData.TenantId, "same");

        Assert.Equal(
            ["ResourceGroup", "WebService", "Widget"],
            response.Items.Select(item => item.SourceType));
        var widgetOnly = await SearchAsync(TestData.TenantId, "same", ["Widget"]);
        Assert.Equal("Widget", Assert.Single(widgetOnly.Items).SourceType);
    }

    [Fact]
    public void UnregisteredApiTypeFilter_IsRejected()
    {
        var validator = new GetSearchRequestValidator(
            Services.GetRequiredService<SearchSourceTypeRegistry>());
        var result = validator.Validate(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = "widget",
            Types = ["NotRegistered"]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(GetSearchRequest.Types));
    }

    [Fact]
    public async Task InitialMigration_CreatesIndexesForExtensibleSchemaOnCleanDatabase()
    {
        await ((IMigration)new SearchDocumentIndexesMigration())
            .Up(Fixture.Database, CancellationToken.None);

        var rowIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchDocuments")
            .Indexes.List().ToListAsync();
        var sourceIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchSourceDocuments")
            .Indexes.List().ToListAsync();
        var versionIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchProjectionVersions")
            .Indexes.List().ToListAsync();

        Assert.Contains(rowIndexes, index =>
            index["name"] == "IX_SearchDocuments_Source_Key" &&
            index["key"].AsBsonDocument.Contains("ResourceTypeId"));
        Assert.Contains(sourceIndexes, index =>
            index["name"] == "IX_SearchSourceDocuments_Source" &&
            index["key"].AsBsonDocument.Contains("ResourceTypeId"));
        Assert.Contains(sourceIndexes, index =>
            index["name"] == "IX_SearchSourceDocuments_SourceApiName_SearchKeys_Value");
        Assert.Contains(versionIndexes, index =>
            index["name"] == "IX_SearchProjectionVersions_Source" &&
            index["unique"].AsBoolean);
    }

    private async Task<PaginatedResponse<SearchHitDto>> SearchAsync(
        string tenantId,
        string query,
        string[]? types = null)
    {
        var result = await Mediator.Send(new GetSearchRequest
        {
            TenantId = tenantId,
            Query = query,
            PageNumber = 1,
            PageSize = 20,
            Types = types ?? []
        });
        return result.AssertHandlerOutcome().Item;
    }

    private static SearchProjectionSnapshot CreateWidget(
        string tenantId,
        string sourceId,
        string title) =>
        CreateSnapshot(
            tenantId,
            ResourceTypeId.Create("third-party/widget"),
            sourceId,
            title);

    private static SearchProjectionSnapshot CreateSnapshot(
        string tenantId,
        ResourceTypeId resourceTypeId,
        string sourceId,
        string title,
        long resourceRevision = 1) =>
        new(
            new SearchProjectionIdentity(tenantId, resourceTypeId, sourceId),
            title,
            null,
            null,
            [new SearchProjectionField(SearchProjectionFieldType.Name, title)],
            resourceRevision);

    private static WebServiceCreatedEvent CreateWebServiceEvent(
        string webServiceId,
        string resourceGroupId,
        string name) =>
        new(
            webServiceId,
            name,
            resourceGroupId,
            ObjectId.GenerateNewId().ToString(),
            1,
            Pole.Features.WebServices.Events.WebServiceLifecycleState.Active,
            TestData.ValidWebServiceSpecSnapshot(),
            new WebServiceExposureSnapshot("Public", "portal"),
            ImmutableHashSet<string>.Empty,
            ImmutableList.Create(new WebServiceDesiredDeploymentSnapshot(
                ObjectId.GenerateNewId().ToString(),
                TestData.DefaultDeploymentTargetId,
                1,
                1,
                "Apply",
                ImmutableList.Create("portal.example.test"))))
        {
            EventCore = EventCore.Create(TestData.TenantId)
        };

    private static WebServiceUpdatedEvent CreateWebServiceUpdatedEvent(
        string webServiceId,
        string resourceGroupId,
        string name) =>
        new(
            webServiceId,
            name,
            resourceGroupId,
            ObjectId.GenerateNewId().ToString(),
            2,
            Pole.Features.WebServices.Events.WebServiceLifecycleState.Active,
            TestData.ValidWebServiceSpecSnapshot(),
            new WebServiceExposureSnapshot("Public", "portal"),
            ImmutableHashSet<string>.Empty,
            ImmutableList.Create(new WebServiceDesiredDeploymentSnapshot(
                ObjectId.GenerateNewId().ToString(),
                TestData.DefaultDeploymentTargetId,
                2,
                2,
                "Apply",
                ImmutableList.Create("renamed.example.test"))))
        {
            EventCore = EventCore.Create(TestData.TenantId)
        };
}
