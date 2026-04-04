using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Endpoints;
using Pole.Features.Search.Common;
using Pole.Features.Search.Consumers;
using Pole.Features.Search.Projection;
using Pole.Features.Search.Services;
using Pole.Features.WebServices.Endpoints;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Search;

public sealed class SearchIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private ISearchService SearchService => Services.GetRequiredService<ISearchService>();
    private IMongoCollection<SearchDocument> SearchDocuments => Fixture.Database.GetCollection<SearchDocument>("SearchDocuments");
    private IMongoCollection<SearchSourceDocument> SearchSourceDocuments => Fixture.Database.GetCollection<SearchSourceDocument>("SearchSourceDocuments");

    [Fact]
    public async Task Create_ProducesMultipleRows_WithFullValuesAndTokens()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        var resourceGroupConsumer = new ResourceGroupCreatedConsumer(DbContext, SearchService);
        await resourceGroupConsumer.Handle(resourceGroup.Id, CancellationToken.None);

        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "web-api", "api.example.com"));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var webConsumer = new WebServiceCreatedConsumer(DbContext, SearchService);
        await webConsumer.Handle(created!.WebServiceId, CancellationToken.None);

        var rows = await SearchDocuments.Find(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId).ToListAsync();
        var sourceDocument = await SearchSourceDocuments.Find(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId).FirstOrDefaultAsync();

        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row.SearchValue == "web-api" && row.Weight == SearchDocumentFactory.NameWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row.SearchValue == "web" && row.Weight == SearchDocumentFactory.NameTokenWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row.SearchValue == "api" && row.Weight == SearchDocumentFactory.NameTokenWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Domain && row.SearchValue == "api.example.com" && row.Weight == SearchDocumentFactory.DomainWeight);
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Domain && row.SearchValue == "example" && row.Weight == SearchDocumentFactory.DomainTokenWeight);
        Assert.DoesNotContain(rows, row => row.SearchValue == "description");
        Assert.NotNull(sourceDocument);
        Assert.Equal("web-api", sourceDocument!.Title);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "web-api" && key.Weight == SearchDocumentFactory.NameWeight);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "api" && key.Weight == SearchDocumentFactory.NameTokenWeight);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "api.example.com" && key.Weight == SearchDocumentFactory.DomainWeight);
    }

    [Fact]
    public async Task Get_ReturnsGroupedResults_AfterPrefixMatching()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        var resourceGroupConsumer = new ResourceGroupCreatedConsumer(DbContext, SearchService);
        await resourceGroupConsumer.Handle(resourceGroup.Id, CancellationToken.None);

        var webCreate = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "alpha-web", "api.alpha.example.com"));
        Assert.True(webCreate.TryGetValue(out var webResponse, out _));
        var webConsumer = new WebServiceCreatedConsumer(DbContext, SearchService);
        await webConsumer.Handle(webResponse!.WebServiceId, CancellationToken.None);

        var redisCreate = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "alpha-redis"));
        Assert.True(redisCreate.TryGetValue(out var redisResponse, out _));
        var redisConsumer = new RedisCreatedConsumer(DbContext, SearchService);
        await redisConsumer.Handle(redisResponse!.RedisId, CancellationToken.None);

        var postgresCreate = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, "alpha-postgres"));
        Assert.True(postgresCreate.TryGetValue(out var postgresResponse, out _));
        var postgresConsumer = new PostgresCreatedConsumer(DbContext, SearchService);
        await postgresConsumer.Handle(postgresResponse!.Id, CancellationToken.None);

        var result = await Mediator.Send(new GetSearchRequest
        {
            Query = "alp",
            PageNumber = 1,
            PageSize = 20
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Contains(response.Items, item => item.SourceType == "ResourceGroup");
        Assert.Contains(response.Items, item => item.SourceType == "WebService");
        Assert.Contains(response.Items, item => item.SourceType == "RedisService");
        Assert.Contains(response.Items, item => item.SourceType == "PostgresCluster");
        Assert.Equal(response.Items.Count(), response.Items.Select(item => $"{item.SourceType}:{item.SourceId}").Distinct().Count());

        var filtered = await Mediator.Send(new GetSearchRequest
        {
            Query = "alp",
            PageNumber = 1,
            PageSize = 20,
            Types = ["WebService"]
        });

        Assert.True(filtered.TryGetValue(out var filteredResponse, out var filteredError));
        Assert.Null(filteredError);
        Assert.NotNull(filteredResponse);
        Assert.Single(filteredResponse.Items);
        Assert.Equal("WebService", filteredResponse.Items.Single().SourceType);

        var serviceQuery = SearchQueryModule.FromRequest(new GetSearchRequest
        {
            Query = "alp",
            PageNumber = 1,
            PageSize = 20
        });
        var serviceResult = await SearchService.Get(serviceQuery, CancellationToken.None);

        Assert.Equal(response.TotalCount, serviceResult.TotalCount);
        Assert.Equal(
            response.Items.Select(item => $"{item.SourceType}:{item.SourceId}"),
            serviceResult.Items.Select(item => $"{item.SourceType}:{item.SourceId}"));
    }

    [Fact]
    public async Task IdMatch_Outranks_NameMatch()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        var webCreate = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "service-123"));
        Assert.True(webCreate.TryGetValue(out var created, out _));
        var webConsumer = new WebServiceCreatedConsumer(DbContext, SearchService);
        await webConsumer.Handle(created!.WebServiceId, CancellationToken.None);

        var secondResourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("beta-shared"));
        var otherCreate = await Mediator.Send(TestData.CreateWebServiceRequest(secondResourceGroup.Id, created.WebServiceId[..8], "other.example.com"));
        Assert.True(otherCreate.TryGetValue(out var otherCreated, out _));
        await webConsumer.Handle(otherCreated!.WebServiceId, CancellationToken.None);

        var result = await Mediator.Send(new GetSearchRequest
        {
            Query = created.WebServiceId[..8].ToLowerInvariant(),
            PageNumber = 1,
            PageSize = 10
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(created.WebServiceId, response.Items.First().SourceId);
    }

    [Fact]
    public async Task ResourceGroupUpdate_RebuildsProjection_WithoutDescriptionRows()
    {
        var createResult = await Mediator.Send(TestData.CreateResourceGroupRequest("ops"));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var createdConsumer = new ResourceGroupCreatedConsumer(DbContext, SearchService);
        await createdConsumer.Handle(created!.Id, CancellationToken.None);

        var updateResult = await Mediator.Send(new UpdateResourceGroupRequest(
            created.Id,
            new UpdateResourceGroupRequestBody(
                Description: "Updated for search",
                Tags: ["team-blue", "prod"])));

        Assert.True(updateResult.TryGetValue(out _, out var error));
        Assert.Null(error);

        var updatedConsumer = new ResourceGroupUpdatedConsumer(DbContext, SearchService);
        await updatedConsumer.Handle(created.Id, CancellationToken.None);

        var rows = await SearchDocuments.Find(row => row.SourceType == SearchDocumentType.ResourceGroup && row.SourceId == created.Id).ToListAsync();
        var sourceDocument = await SearchSourceDocuments.Find(row => row.SourceType == SearchDocumentType.ResourceGroup && row.SourceId == created.Id).FirstOrDefaultAsync();
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Tag && row.SearchValue == "team-blue");
        Assert.DoesNotContain(rows, row => row.SearchValue == "updated");
        Assert.NotNull(sourceDocument);
        Assert.Contains(sourceDocument!.SearchKeys, key => key.Value == "team-blue" && key.Weight == SearchDocumentFactory.TagWeight);
        Assert.DoesNotContain(sourceDocument.SearchKeys, key => key.Value == "updated");

        var searchResult = await Mediator.Send(new GetSearchRequest
        {
            Query = "team",
            PageNumber = 1,
            PageSize = 10
        });

        Assert.True(searchResult.TryGetValue(out var response, out var searchError));
        Assert.Null(searchError);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal(created.Id, response.Items.Single().SourceId);
    }

    [Fact]
    public async Task WebServiceDelete_RemovesAllRows()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("apps"));
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "portal", "portal.example.com"));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var createdConsumer = new WebServiceCreatedConsumer(DbContext, SearchService);
        await createdConsumer.Handle(created!.WebServiceId, CancellationToken.None);

        var dnsRecord = await DbContext.DnsRecords.Find(record => record.ResourceId == created.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(dnsRecord);

        var deleteResult = await Mediator.Send(new DeleteWebServiceRequest(created.WebServiceId));
        Assert.True(deleteResult.TryGetValue(out _, out var deleteError));
        Assert.Null(deleteError);

        var deletedConsumer = new WebServiceDeletedConsumer(SearchService);
        await deletedConsumer.Handle(created.WebServiceId, dnsRecord!.Id, CancellationToken.None);

        var serviceRows = await SearchDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId);
        var dnsRows = await SearchDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.DnsRecord && row.SourceId == dnsRecord.Id);
        var serviceSourceRows = await SearchSourceDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId);
        var dnsSourceRows = await SearchSourceDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.DnsRecord && row.SourceId == dnsRecord.Id);

        Assert.Equal(0L, serviceRows);
        Assert.Equal(0L, dnsRows);
        Assert.Equal(0L, serviceSourceRows);
        Assert.Equal(0L, dnsSourceRows);
    }
}
