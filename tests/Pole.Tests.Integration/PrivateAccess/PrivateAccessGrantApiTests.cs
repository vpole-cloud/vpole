// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Endpoints;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class PrivateAccessGrantApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task ResourceNameLookup_ReturnsOnlyOwnActiveGrantForExactTenantResource()
    {
        await SetViewerAsync();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var ownResource = RedisFactory.Create(
            Data.TenantId,
            "orders",
            ObjectId.GenerateNewId().ToString(),
            new RedisSpec(new Storage(1, TestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions);
        var otherResource = RedisFactory.Create(
            otherTenantId,
            "orders",
            ObjectId.GenerateNewId().ToString(),
            new RedisSpec(new Storage(1, TestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions);
        await Fixture.Database.GetCollection<Redis>("RedisServices").InsertManyAsync([ownResource, otherResource]);

        var deploymentId = ObjectId.GenerateNewId().ToString();
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(AppliedDeployment(ownResource.Id, deploymentId));

        var own = PrivateAccessGrantFactory.Create(
            Data.TenantId,
            Data.UserId,
            "pole/redis",
            ownResource.Id,
            deploymentId,
            "redis",
            Data.UserId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1));
        var anotherTenant = PrivateAccessGrantFactory.Create(
            otherTenantId,
            Data.UserId,
            "pole/redis",
            otherResource.Id,
            ObjectId.GenerateNewId().ToString(),
            "redis",
            Data.UserId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1));
        var anotherSubject = own with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            SubjectUserId = "someone-else",
        };
        await Fixture.Database
            .GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertManyAsync([own, anotherTenant, anotherSubject]);

        var response = await Client.GetAsync("/api/v1/private-access/resources/redis/grants?name=orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<PrivateAccessResourceGrantOptions>();
        Assert.Equal(own.Id, Assert.Single(Assert.IsType<PrivateAccessResourceGrantOptions>(options).Items).GrantId);

        using var nameWithSlash = await Client.GetAsync(
            "/api/v1/private-access/resources/redis/grants?name=orders%2F2026");
        Assert.Equal(HttpStatusCode.OK, nameWithSlash.StatusCode);
    }

    [Fact]
    public async Task ResourceNameLookup_ExcludesStaleDeploymentAndRemovedEndpoint()
    {
        await SetViewerAsync();
        var resource = RedisFactory.Create(
            Data.TenantId,
            "orders",
            ObjectId.GenerateNewId().ToString(),
            new RedisSpec(new Storage(1, TestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions);
        await Fixture.Database.GetCollection<Redis>("RedisServices").InsertOneAsync(resource);

        var deploymentId = ObjectId.GenerateNewId().ToString();
        var applied = AppliedDeployment(resource.Id, deploymentId);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(applied);

        var missingDeployment = GrantForResource(resource.Id, ObjectId.GenerateNewId().ToString(), "redis");
        var removedEndpoint = GrantForResource(resource.Id, deploymentId, "removed");
        await Fixture.Database
            .GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertManyAsync([missingDeployment, removedEndpoint]);

        var unavailable = await GetResourceGrantOptionsAsync();
        Assert.Empty(unavailable.Items);

        var available = GrantForResource(resource.Id, deploymentId, "redis");
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants").InsertOneAsync(available);
        var current = await GetResourceGrantOptionsAsync();
        Assert.Equal(available.Id, Assert.Single(current.Items).GrantId);

        var pending = applied with
        {
            State = new PendingResourceDeploymentState(
                applied.DesiredGeneration,
                ResourceDeploymentOperation.Apply,
                AttemptCount: 0,
                DateTime.UtcNow),
        };
        await Fixture.DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deploymentId, pending);
        var noLongerApplied = await GetResourceGrantOptionsAsync();
        Assert.Empty(noLongerApplied.Items);
    }

    [Fact]
    public async Task Viewer_ListsOnlyOwnTenantGrants_AndCannotManageEvenWithPlatformAdminClaim()
    {
        await SetViewerAsync();
        var own = Grant(Data.TenantId, Data.UserId);
        await Fixture.Database
            .GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertManyAsync(
            [
                own,
                Grant(Data.TenantId, "someone-else"),
                Grant(ObjectId.GenerateNewId().ToString(), Data.UserId),
            ]);

        var response = await Client.GetAsync("/api/v1/private-access/grants");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var grants = await response.Content.ReadFromJsonAsync<PrivateAccessGrantDto[]>();
        Assert.Equal(own.Id, Assert.Single(Assert.IsType<PrivateAccessGrantDto[]>(grants)).Id);

        var forbidden = await Client.GetAsync("/api/v1/private-access/grants/manage");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var platformAdmin = await Client.GetAsync("/api/v1/private-access/grants/manage");
        Assert.Equal(HttpStatusCode.Forbidden, platformAdmin.StatusCode);
    }

    [Fact]
    public async Task SessionIssue_RejectsAnotherSubjectsGrant_ThroughRealTenantPipeline()
    {
        await SetViewerAsync();
        var other = Grant(Data.TenantId, "someone-else");
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants").InsertOneAsync(other);

        var response = await Client.PostAsync($"/api/v1/private-access/grants/{other.Id}/sessions", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("private-access.subject-mismatch", await response.Content.ReadAsStringAsync());
    }

    private Task SetViewerAsync()
    {
        var filter = Builders<Tenant>.Filter.Eq(item => item.Id, Data.TenantId);
        var members = ImmutableList.Create(
            TenantFactory.CreateMember(Data.TenantId, Data.UserId, Data.UserEmail, TenantRole.Viewer));
        return Fixture.DbContext.Tenants.UpdateOneAsync(
            filter,
            Builders<Tenant>.Update.Set(item => item.Members, members));
    }

    private async Task<PrivateAccessResourceGrantOptions> GetResourceGrantOptionsAsync()
    {
        using var response = await Client.GetAsync("/api/v1/private-access/resources/redis/grants?name=orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<PrivateAccessResourceGrantOptions>(
            await response.Content.ReadFromJsonAsync<PrivateAccessResourceGrantOptions>());
    }

    private PrivateAccessGrant GrantForResource(
        string resourceId,
        string deploymentId,
        string endpointId) => PrivateAccessGrantFactory.Create(
        Data.TenantId,
        Data.UserId,
        "pole/redis",
        resourceId,
        deploymentId,
        endpointId,
        Data.UserId,
        DateTime.UtcNow,
        DateTime.UtcNow.AddHours(1));

    private ResourceDeployment AppliedDeployment(string resourceId, string deploymentId)
    {
        var now = DateTime.UtcNow;
        var deployment = ResourceDeploymentFactory.Create(
            Data.TenantId,
            ResourceTypeId.Create("pole/redis"),
            resourceId,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(
                ObjectId.GenerateNewId().ToString(),
                TestData.DefaultDeploymentTargetId,
                "pole-private-access-test",
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ingress: null,
                storage: null,
                ResourceDeploymentTestData.Compute(),
                now),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = deploymentId,
                CreatedAtUtc = now,
            });
        return deployment with
        {
            State = new AppliedResourceDeploymentState(1, now),
        };
    }

    private PrivateAccessGrant Grant(string tenantId, string subject) => PrivateAccessGrantFactory.Create(
        tenantId,
        subject,
        "pole/redis",
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        "redis",
        Data.UserId,
        DateTime.UtcNow,
        DateTime.UtcNow.AddHours(1));
}
