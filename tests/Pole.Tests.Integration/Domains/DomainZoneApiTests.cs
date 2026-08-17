using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainZoneApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private const string OtherTenantId = "507f1f77bcf86cd799439099";

    [Fact]
    public async Task GetById_ReturnsTenantZoneThroughTypedOutcome()
    {
        using var created = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdZone = await created.Content
            .ReadFromJsonAsync<TenantDomainZoneSnapshot>();
        Assert.NotNull(createdZone);

        using var response = await Client.GetAsync(
            $"/api/v1/domain-zones/{createdZone.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var zone = await response.Content
            .ReadFromJsonAsync<TenantDomainZoneSnapshot>();
        Assert.NotNull(zone);
        Assert.Equal(createdZone.Id, zone.Id);
        Assert.Equal(createdZone.TenantId, zone.TenantId);
        Assert.Equal(createdZone.DeploymentTargetId, zone.DeploymentTargetId);
        Assert.Equal(createdZone.Domain, zone.Domain);
        Assert.Equal(createdZone.Revision, zone.Revision);
    }

    [Fact]
    public async Task GetById_ReturnsNotFoundWithoutLeakingAnotherTenantZone()
    {
        var foreignZone = TenantDomainZoneFactory.CreateManagedReservation(
            OtherTenantId,
            TestData.DefaultDeploymentTargetId,
            "foreign",
            DnsName.Create("apps.test.local"),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
        await Fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .InsertOneAsync(foreignZone);

        using var response = await Client.GetAsync(
            $"/api/v1/domain-zones/{foreignZone.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "https://pole.dev/problems/domain-zone-not-found",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "domains.zone.not-found",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            foreignZone.Id,
            problem.RootElement.GetProperty("domainZoneId").GetString());
        Assert.Equal(
            (int)HttpStatusCode.NotFound,
            problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task GetAll_ReturnsTenantZonesThroughOutcomeHandler()
    {
        using var created = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await Client.GetAsync("/api/v1/domain-zones/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var zones = await response.Content
            .ReadFromJsonAsync<IReadOnlyList<TenantDomainZoneSnapshot>>();
        Assert.NotNull(zones);
        var zone = Assert.Single(zones);
        Assert.Equal(TestData.TenantId, zone.TenantId);
        Assert.Equal("company.apps.test.local", zone.Domain);
    }

    [Fact]
    public async Task Create_ReturnsCreatedSnapshotAndCommitsReservation()
    {
        using var response = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var zone = await response.Content.ReadFromJsonAsync<TenantDomainZoneSnapshot>();
        Assert.NotNull(zone);
        Assert.Equal(TestData.TenantId, zone.TenantId);
        Assert.Equal(TestData.DefaultDeploymentTargetId, zone.DeploymentTargetId);
        Assert.Equal("company.apps.test.local", zone.Domain);
        Assert.Equal(
            1L,
            await ZoneCountAsync());
    }

    [Fact]
    public async Task Create_ReturnsProviderProblemWithoutWritingForUnknownTarget()
    {
        var deploymentTargetId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

        using var response = await CreateAsync(deploymentTargetId, "company");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(
            response,
            "https://pole.dev/problems/deployment-target-not-found",
            "deployment-targets.not-found",
            deploymentTargetId);
        Assert.Equal(0L, await ZoneCountAsync());
    }

    [Fact]
    public async Task Create_ReturnsProviderProblemWithoutWritingWhenTargetRejectsReservations()
    {
        RejectNewDeploymentTargetWorkloads();

        using var response = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(
            response,
            "https://pole.dev/problems/deployment-target-new-workloads-disabled",
            "deployment-targets.new-workloads-disabled",
            TestData.DefaultDeploymentTargetId);
        Assert.Equal(0L, await ZoneCountAsync());
    }

    [Fact]
    public async Task Create_ReturnsDomainProblemAndKeepsFirstReservationOnDuplicate()
    {
        using var first = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var duplicate = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(
            await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(
            "https://pole.dev/problems/domain-zone-already-reserved",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "domains.zone.already-reserved",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "company.apps.test.local",
            problem.RootElement.GetProperty("domain").GetString());
        Assert.Equal(1L, await ZoneCountAsync());
    }

    [Fact]
    public async Task ValidationPipeline_ShortCircuitsOutcomeHandlerWithoutWriting()
    {
        using var response = await CreateAsync("not-an-object-id", "not valid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await ZoneCountAsync());
    }

    [Fact]
    public async Task AuthorizationPipeline_ShortCircuitsOutcomeHandlerWithoutWriting()
    {
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        using var response = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            "company");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await ZoneCountAsync());
    }

    private Task<HttpResponseMessage> CreateAsync(
        string deploymentTargetId,
        string label) =>
        Client.PostAsJsonAsync(
            "/api/v1/domain-zones/",
            new { deploymentTargetId, label });

    private Task<long> ZoneCountAsync() =>
        Fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty);

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string type,
        string code,
        string deploymentTargetId)
    {
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(type, problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            deploymentTargetId,
            problem.RootElement.GetProperty("deploymentTargetId").GetString());
        Assert.Equal(
            (int)response.StatusCode,
            problem.RootElement.GetProperty("status").GetInt32());
    }

    private async Task SetCurrentUserRoleAsync(TenantRole role)
    {
        var tenant = await Fixture.DbContext.Tenants
            .Find(item => item.Id == TestData.TenantId)
            .SingleAsync();
        var updated = tenant with
        {
            Members = tenant.Members
                .Select(member => member.UserId == TestData.UserId
                    ? member with { Role = role }
                    : member)
                .ToImmutableList()
        };
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == TestData.TenantId,
            updated);
    }
}
