using Pole.Domain.Models;
using Pole.Feature.DomainNames.Endpoints;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DomainNames;

public sealed class DomainNameIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task GetList_ReturnsPaginatedDnsRecords()
    {
        await DbContext.InsertDnsRecordAsync(new DnsRecord("alpha.example.com", ResourceType.WebService, "web-1", "alpha", "rg-1"));
        await DbContext.InsertDnsRecordAsync(new DnsRecord("beta.example.com", ResourceType.WebService, "web-2", "beta", "rg-1"));

        var result = await Mediator.Send(new GetDomainNamesRequest
        {
            PageNumber = 1,
            PageSize = 1
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal(2L, response.TotalCount);
        Assert.True(response.HasNext);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_AndMatchesSubstrings()
    {
        await DbContext.InsertDnsRecordAsync(new DnsRecord("Admin.Example.com", ResourceType.WebService, "web-1", "admin", "rg-1"));
        await DbContext.InsertDnsRecordAsync(new DnsRecord("worker.example.com", ResourceType.WebService, "web-2", "worker", "rg-1"));

        var result = await Mediator.Send(new GetDomainNamesRequest
        {
            Search = "example",
            PageNumber = 1,
            PageSize = 10
        });

        Assert.True(result.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(2, response.Items.Count());

        var adminOnly = await Mediator.Send(new GetDomainNamesRequest
        {
            Search = "ADMIN",
            PageNumber = 1,
            PageSize = 10
        });

        Assert.True(adminOnly.TryGetValue(out var adminResponse, out var secondError));
        Assert.Null(secondError);
        Assert.NotNull(adminResponse);
        Assert.Single(adminResponse.Items);
        Assert.Equal("Admin.Example.com", adminResponse.Items.Single().Name);
    }
}
