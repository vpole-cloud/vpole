// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceDeploymentSetApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Generic_inventory_filters_exact_tenant_type_and_canonical_resource_identity_for_viewers()
    {
        var resourceId = ObjectId.GenerateNewId().ToString();
        var type = ResourceTypeId.Create("pole/test-discovery");
        var matching = Create(Data.TenantId, type, resourceId);
        await Fixture.DbContext.ResourceDeployments.InsertManyAsync(
        [
            matching,
            Create(ObjectId.GenerateNewId().ToString(), type, resourceId),
            Create(Data.TenantId, ResourceTypeId.Create("pole/other"), resourceId),
            Create(Data.TenantId, type, ObjectId.GenerateNewId().ToString()),
        ]);
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            x => x.Id == tenant.Id,
            tenant with
            {
                Members = ImmutableList.Create(
                    TenantFactory.CreateMember(Data.TenantId, Data.UserId, Data.UserEmail, TenantRole.Viewer)),
            });
        var uri
            = $"/api/v1/resource-deployments/?resourceType=pole%2Ftest-discovery&resourceId={resourceId.ToUpperInvariant()}";
        var deployments = await Client.GetFromJsonAsync<PlacementSummaryDto[]>(uri);
        Assert.Equal(matching.Id, Assert.Single(deployments!).DeploymentId);
        var body = await Client.GetStringAsync(uri);
        Assert.DoesNotContain("private-test-namespace", body);
        Assert.Empty(
            (await Client.GetFromJsonAsync<PlacementSummaryDto[]>(
                $"/api/v1/resource-deployments/?resourceType=pole%2Funknown&resourceId={resourceId}"))!);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(uri)).StatusCode);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, ObjectId.GenerateNewId().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(uri)).StatusCode);
    }

    [Theory]
    [InlineData("invalid", "507f1f77bcf86cd799439011")]
    [InlineData("pole/test", "invalid")]
    public async Task Rejects_invalid_resource_query(string type, string id) => Assert.Equal(
        HttpStatusCode.BadRequest,
        (await Client.GetAsync(
            $"/api/v1/resource-deployments/?resourceType={Uri.EscapeDataString(type)}&resourceId={id}")).StatusCode);

    private static ResourceDeployment Create(
        string tenant,
        ResourceTypeId type,
        string resourceId) => ResourceDeploymentFactory.Create(
        tenant,
        type,
        resourceId,
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        ResolvedResourcePlacement.Create(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "private-test-namespace",
            1,
            1,
            ResolvedIngressConfiguration.Create("traefik", "issuer"),
            null,
            ResourceDeploymentTestData.Compute(),
            DateTime.UtcNow),
        ResourcePlacementCapability.SingleTarget,
        runtimeCellReady: true,
        ResourceDeploymentFactory.DefaultOptions with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            CreatedAtUtc = DateTime.UtcNow,
        });
}
