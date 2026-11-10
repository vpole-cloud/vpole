using System.Net;
using MongoDB.Bson;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceGroupReadApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task List_PaginatesInStableNameOrderAndSearchesOnlySelectedTenant()
    {
        var zed = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup() with { Name = "Zed" });
        var alpha = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup() with { Name = "Alpha" });
        await Fixture.DbContext.InsertResourceGroupAsync(
            Data.SeedResourceGroup(tenantId: ObjectId.GenerateNewId().ToString()) with { Name = "A foreign group" });
        var first = await Client.GetFromJsonAsync<PaginatedResponse<ResourceGroupDto>>(
            "/api/v1/resource-groups?pageNumber=1&pageSize=1");
        var second = await Client.GetFromJsonAsync<PaginatedResponse<ResourceGroupDto>>(
            "/api/v1/resource-groups?pageNumber=2&pageSize=1");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(alpha.Id, Assert.Single(first.Items).Id);
        Assert.Equal(zed.Id, Assert.Single(second.Items).Id);
        var filtered
            = await Client.GetFromJsonAsync<PaginatedResponse<ResourceGroupDto>>(
                "/api/v1/resource-groups?pageNumber=1&pageSize=1&search=Zed");
        Assert.NotNull(filtered);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(zed.Id, Assert.Single(filtered.Items).Id);
    }

    [Fact]
    public async Task Get_ReturnsGroupAndRegistrationCount()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        await Fixture.DbContext.ResourceRegistrations.InsertOneAsync(
            ResourceRegistrationFactory.Create(
                group.TenantId,
                group.Id,
                ResourceTypeId.Create("third-party/widget"),
                "widget-1",
                "Widget",
                "third-party",
                new ResourceRegistrationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow)));
        using var response = await Client.GetAsync($"/api/v1/resource-groups/{group.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ResourceGroupDto>();
        Assert.NotNull(result);
        Assert.Equal(group.Id, result.Id);
        Assert.Equal(group.Name, result.Name);
        Assert.Equal(1, result.ResourceCount);
    }

    [Fact]
    public async Task Get_DoesNotExposeAnotherTenantsGroup()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(
            Data.SeedResourceGroup(tenantId: ObjectId.GenerateNewId().ToString()));
        using var response = await Client.GetAsync($"/api/v1/resource-groups/{group.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_MissingGroupReturnsNotFound()
    {
        using var response = await Client.GetAsync($"/api/v1/resource-groups/{ObjectId.GenerateNewId()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_InvalidIdReturnsValidationProblem()
    {
        using var response = await Client.GetAsync("/api/v1/resource-groups/invalid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("errors", body);
    }
}
