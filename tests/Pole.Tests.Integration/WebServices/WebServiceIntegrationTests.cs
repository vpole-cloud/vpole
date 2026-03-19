using MongoDB.Driver;
using Pole.Features.WebServices.Endpoints;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_Succeeds_WhenResourceGroupExists_AndDomainIsFree()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(resourceGroup.Id);

        var result = await Mediator.Send(request);

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);

        var savedWebService = await DbContext.WebServices.Find(service => service.Id == response.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(savedWebService);

        var savedDns = await DbContext.DnsRecords.Find(record => record.ResourceId == response.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(savedDns);
        Assert.Equal(request.Body.DomainName, savedDns.Name);

        var outboxCount = await DbContext.CountOutboxMessagesAsync("create-web-service-requested");
        Assert.Equal(1L, outboxCount);
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenResourceGroupDoesNotExist()
    {
        var request = TestData.CreateWebServiceRequest("507f1f77bcf86cd799439011");

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
        Assert.Contains("507f1f77bcf86cd799439011", error.Message);
    }

    [Fact]
    public async Task Create_ReturnsConflict_WhenDomainAlreadyExists()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        await DbContext.InsertDnsRecordAsync(TestData.SeedDnsRecord(resourceGroup.Id, "existing-resource"));
        var request = TestData.CreateWebServiceRequest(resourceGroup.Id, domainName: "existing.example.com");

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<ConflictFailure>(error);
        Assert.Contains("already in use", error.Message);
    }

    [Fact]
    public async Task GetList_FiltersBySearch_AndAppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, name: "api-alpha", domainName: "alpha.example.com"));
        await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, name: "api-beta", domainName: "beta.example.com"));

        var result = await Mediator.Send(new GetWebServicesRequest
        {
            Search = "beta",
            PageNumber = 1,
            PageSize = 1
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal(1L, response.TotalCount);
        Assert.Equal("api-beta", response.Items.Single().Name);
    }

    [Fact]
    public async Task Delete_RemovesWebService_AndDnsRecord()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var deleteResult = await Mediator.Send(new DeleteWebServiceRequest(created!.WebServiceId));

        Assert.True(deleteResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(created.WebServiceId, response.Id);

        var deletedService = await DbContext.WebServices.Find(service => service.Id == created.WebServiceId).FirstOrDefaultAsync();
        var deletedDns = await DbContext.DnsRecords.Find(record => record.ResourceId == created.WebServiceId).FirstOrDefaultAsync();

        Assert.Null(deletedService);
        Assert.Null(deletedDns);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("delete-web-service"));
    }
}
