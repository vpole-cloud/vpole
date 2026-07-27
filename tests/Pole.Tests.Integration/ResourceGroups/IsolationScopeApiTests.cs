using System.Net;
using System.Net.Http.Json;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class IsolationScopeApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task NestedEndpoints_BindAsParameters_AndReturnDefaultAndAdditionalScopes()
    {
        using var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest().Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupDto>();
        Assert.NotNull(resourceGroup);
        Assert.False(string.IsNullOrWhiteSpace(resourceGroup.DefaultIsolationScopeId));

        using var createResponse = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = "Production" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var additional = await createResponse.Content.ReadFromJsonAsync<IsolationScopeDto>();
        Assert.NotNull(additional);

        using var listResponse = await Client.GetAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var scopes = await listResponse.Content.ReadFromJsonAsync<List<IsolationScopeDto>>();
        Assert.NotNull(scopes);
        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, scope =>
            scope.Id == resourceGroup.DefaultIsolationScopeId && scope.IsDefault);

        using var getResponse = await Client.GetAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes/{additional.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Endpoints_ReturnValidationAndConflictFailures()
    {
        using var invalidRoute = await Client.GetAsync(
            "/api/v1/resource-groups/not-an-object-id/isolation-scopes");
        Assert.Equal(HttpStatusCode.BadRequest, invalidRoute.StatusCode);

        using var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest().Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupDto>();
        Assert.NotNull(resourceGroup);

        using var reserved = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = " DEFAULT " });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        using var first = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = "Production" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = " production " });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }
}
