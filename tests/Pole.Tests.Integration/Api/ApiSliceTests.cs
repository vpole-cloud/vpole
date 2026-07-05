using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ApiSliceTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task CreateResourceGroup_ReturnsCreated()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/resource-groups/", TestData.CreateResourceGroupRequest().Body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ResourceGroupCreatedResponse>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload.Id));
    }

    [Fact]
    public async Task CreateRedisService_ReturnsValidationProblem_ForInvalidName()
    {
        var resourceGroupResponse = await Client.PostAsJsonAsync("/api/v1/resource-groups/", TestData.CreateResourceGroupRequest("api-rg").Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupCreatedResponse>();
        Assert.NotNull(resourceGroup);

        var invalidRequest = new CreateRedisServiceRequestBody("Bad Name", resourceGroup.Id, TestData.ValidRedisSpec(), []);

        var response = await Client.PostAsJsonAsync("/api/v1/redis-services/", invalidRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("Body.Name", out _));
    }

    [Fact]
    public async Task GetWebServiceById_ReturnsNotFound_ForMissingEntity()
    {
        var response = await Client.GetAsync("/api/v1/web-services/507f1f77bcf86cd799439013");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateWebService_ReturnsConflict_WhenDomainExists()
    {
        var resourceGroupResponse = await Client.PostAsJsonAsync("/api/v1/resource-groups/", TestData.CreateResourceGroupRequest("web-rg").Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupCreatedResponse>();
        Assert.NotNull(resourceGroup);

        await Client.PostAsJsonAsync("/api/v1/web-services/", TestData.CreateWebServiceRequest(resourceGroup.Id, domainName: "dup.example.com").Body);
        var response = await Client.PostAsJsonAsync("/api/v1/web-services/", TestData.CreateWebServiceRequest(resourceGroup.Id, name: "second-web", domainName: "dup.example.com").Body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetSupportedPostgresVersions_ReturnsConfiguredRange()
    {
        var response = await Client.GetAsync("/api/v1/postgres/supported-versions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<SupportedPostgresVersionsResponse>();
        Assert.NotNull(payload);
        Assert.Equal(16, payload.From);
        Assert.Equal(17, payload.To);
    }

    private sealed record ResourceGroupCreatedResponse(string Id, string Name, string Description, IReadOnlyCollection<string> Tags);
    private sealed record SupportedPostgresVersionsResponse(int From, int To);
}

