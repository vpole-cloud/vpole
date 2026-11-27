// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class IsolationScopeApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task NestedEndpoints_BindAsParameters_AndReturnDefaultAndAdditionalScopes()
    {
        using var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            Data.CreateResourceGroupRequest().Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupDto>();
        Assert.NotNull(resourceGroup);
        Assert.False(string.IsNullOrWhiteSpace(resourceGroup.DefaultIsolationScopeId));

        using var createResponse = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = "production" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var additional = await createResponse.Content.ReadFromJsonAsync<IsolationScopeDto>();
        Assert.NotNull(additional);

        using var listResponse = await Client.GetAsync($"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var scopes = await listResponse.Content.ReadFromJsonAsync<List<IsolationScopeDto>>();
        Assert.NotNull(scopes);
        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, scope => scope.Id == resourceGroup.DefaultIsolationScopeId && scope.IsDefault);

        using var getResponse = await Client.GetAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes/{additional.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Endpoints_ReturnValidationAndConflictFailures()
    {
        using var invalidRoute = await Client.GetAsync("/api/v1/resource-groups/not-an-object-id/isolation-scopes");
        Assert.Equal(HttpStatusCode.BadRequest, invalidRoute.StatusCode);

        using var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            Data.CreateResourceGroupRequest().Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupDto>();
        Assert.NotNull(resourceGroup);

        using var reserved = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = " DEFAULT " });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        using var first = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = "production" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await Client.PostAsJsonAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes",
            new { name = "production" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task DeleteEndpoint_ReturnsNoContentConflictAndAccepted()
    {
        var resourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var emptyScope = IsolationScopeFactory.CreateAdditional(
            resourceGroup.TenantId,
            resourceGroup.Id,
            "empty",
            IsolationScopeFactory.DefaultOptions);
        var occupiedScope = IsolationScopeFactory.CreateAdditional(
            resourceGroup.TenantId,
            resourceGroup.Id,
            "occupied",
            IsolationScopeFactory.DefaultOptions);
        await Fixture.DbContext.IsolationScopes.InsertManyAsync([emptyScope, occupiedScope]);
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            occupiedScope.Id,
            "507f1f77bcf86cd799439071",
            "pole-delete-api",
            RuntimeCellFactory.DefaultOptions);
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(cell);

        using var emptyResponse = await Client.DeleteAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes/{emptyScope.Id}");
        using var defaultResponse = await Client.DeleteAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes/{resourceGroup.DefaultIsolationScopeId}");
        using var occupiedResponse = await Client.DeleteAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/isolation-scopes/{occupiedScope.Id}");

        Assert.Equal(HttpStatusCode.NoContent, emptyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, defaultResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, occupiedResponse.StatusCode);
    }
}
