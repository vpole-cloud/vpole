using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class ResourceRegistrationApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task List_ReturnsTenantScopedRegistrationPage()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        await Fixture.DbContext.ResourceRegistrations.InsertOneAsync(
            ResourceRegistrationFactory.Create(
                group.TenantId,
                group.Id,
                ResourceTypeId.Create("pole/redis"),
                "redis-1",
                "Cache",
                null,
                new ResourceRegistrationFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    DateTime.UtcNow)));

        using var response = await Client.GetAsync(
            $"/api/v1/resource-groups/{group.Id}/resources?pageNumber=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<
            PaginatedResponse<ResourceRegistrationDto>>();
        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        var registration = Assert.Single(page.Items);
        Assert.Equal("pole/redis", registration.ResourceTypeId);
        Assert.Equal("redis-1", registration.ResourceId);
    }

    [Fact]
    public async Task List_ReturnsNotFound_ForAnotherTenantsGroup()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup(
                tenantId: ObjectId.GenerateNewId().ToString()));

        using var response = await Client.GetAsync(
            $"/api/v1/resource-groups/{group.Id}/resources");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
