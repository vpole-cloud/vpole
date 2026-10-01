using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Endpoints;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

public sealed class PrivateAccessGrantApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task ResourceNameLookup_ReturnsOnlyOwnActiveGrantForExactTenantResource()
    {
        await SetViewerAsync();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var ownResource = RedisFactory.Create(
            TestData.TenantId,
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
        await Fixture.Database.GetCollection<Redis>("RedisServices")
            .InsertManyAsync([ownResource, otherResource]);

        var own = PrivateAccessGrantFactory.Create(
            TestData.TenantId, TestData.UserId, "pole/redis", ownResource.Id,
            ObjectId.GenerateNewId().ToString(), "redis", TestData.UserId,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var anotherTenant = PrivateAccessGrantFactory.Create(
            otherTenantId, TestData.UserId, "pole/redis", otherResource.Id,
            ObjectId.GenerateNewId().ToString(), "redis", TestData.UserId,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var anotherSubject = own with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            SubjectUserId = "someone-else"
        };
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertManyAsync([own, anotherTenant, anotherSubject]);

        var response = await Client.GetAsync(
            "/api/v1/private-access/resources/redis/orders/grants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<PrivateAccessResourceGrantOptions>();
        Assert.Equal(own.Id, Assert.Single(Assert.IsType<PrivateAccessResourceGrantOptions>(options).Items).GrantId);
    }

    [Fact]
    public async Task Viewer_ListsOnlyOwnTenantGrants_AndCannotManageEvenWithPlatformAdminClaim()
    {
        await SetViewerAsync();
        var own = Grant(TestData.TenantId, TestData.UserId);
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertManyAsync([
                own,
                Grant(TestData.TenantId, "someone-else"),
                Grant(ObjectId.GenerateNewId().ToString(), TestData.UserId)
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
        var other = Grant(TestData.TenantId, "someone-else");
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertOneAsync(other);

        var response = await Client.PostAsync(
            $"/api/v1/private-access/grants/{other.Id}/sessions", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("private-access.subject-mismatch", await response.Content.ReadAsStringAsync());
    }

    private Task SetViewerAsync()
    {
        var filter = Builders<Tenant>.Filter.Eq(item => item.Id, TestData.TenantId);
        var members = ImmutableList.Create(TenantFactory.CreateMember(
            TestData.TenantId, TestData.UserId, TestData.UserEmail, TenantRole.Viewer));
        return Fixture.DbContext.Tenants.UpdateOneAsync(filter,
            Builders<Tenant>.Update.Set(item => item.Members, members));
    }

    private static PrivateAccessGrant Grant(string tenantId, string subject) =>
        PrivateAccessGrantFactory.Create(
            tenantId,
            subject,
            "pole/redis",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "redis",
            TestData.UserId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1));
}
