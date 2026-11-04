using Pole.Features.ResourceDeployments.Contracts;
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
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainZoneApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private const string OtherTenantId = "507f1f77bcf86cd799439099";

    [Fact]
    public async Task Binding_usage_is_paged_tenant_scoped_and_resolves_exact_owner_and_pending_target_for_viewer()
    {
        var zone = await CreateZoneAsync("owners");
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("domain-owners"));
        using var create = await Client.PostAsJsonAsync("/api/v1/web-services", TestData.CreateWebServiceRequest(group.Id).Body);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var service = (await create.Content.ReadFromJsonAsync<CreateWebServiceResponse>())!;
        var placement = Assert.Single(service.Deployments);
        foreach (var label in new[] { "alpha", "beta" })
        {
            using var attach = await Client.PostAsJsonAsync($"/api/v1/web-services/{service.WebServiceId}/deployments/{placement.DeploymentId}/hostnames", new { tenantDomainZoneId = zone.Id, relativeName = label });
            Assert.Equal(HttpStatusCode.Accepted, attach.StatusCode);
        }
        var foreign = HostnameBindingFactory.CreateCustom(
            OtherTenantId, zone.Id, TestData.DefaultDeploymentTargetId, ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "foreign-canary", DnsName.Create(zone.Domain),
            new HostnameBindingFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings").InsertOneAsync(foreign);
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        using var first = await Client.GetAsync($"/api/v1/domain-zones/{zone.Id}/bindings?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = (await first.Content.ReadFromJsonAsync<GetDomainZoneBindingsResponse>())!;
        Assert.Equal(2, page.TotalCount);
        var owner = Assert.Single(page.Items);
        Assert.Equal("alpha." + zone.Domain, owner.Binding.Hostname);
        Assert.Equal("web-api", owner.ResourceDisplayName);
        Assert.Equal(group.Id, owner.ResourceGroupId);
        Assert.Equal(placement.DeploymentId, owner.Placement!.DeploymentId);
        Assert.Equal(TestData.DefaultDeploymentTargetId, owner.Placement.DeploymentTargetId);
        Assert.Equal(ResourceDeploymentStatus.WaitingForRuntimeCell, owner.Placement.Status);
        Assert.DoesNotContain("foreign-canary", await first.Content.ReadAsStringAsync());
        using var second = await Client.GetAsync($"/api/v1/domain-zones/{zone.Id}/bindings?pageNumber=2&pageSize=1");
        var next = (await second.Content.ReadFromJsonAsync<GetDomainZoneBindingsResponse>())!;
        Assert.Equal("beta." + zone.Domain, Assert.Single(next.Items).Binding.Hostname);
    }

    [Fact]
    public async Task Binding_usage_hides_foreign_zones_and_bounds_page_size()
    {
        var zone = await CreateZoneAsync("usage-bounds");
        using var invalid = await Client.GetAsync($"/api/v1/domain-zones/{zone.Id}/bindings?pageNumber=1&pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones")
            .UpdateOneAsync(item => item.Id == zone.Id, Builders<TenantDomainZone>.Update.Set(item => item.TenantId, OtherTenantId));
        using var hidden = await Client.GetAsync($"/api/v1/domain-zones/{zone.Id}/bindings");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain(zone.Domain, await hidden.Content.ReadAsStringAsync());
    }

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
            "tag:pole.dev,2026:problem:domains.zone.not-found",
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
    public async Task Delete_ReturnsNoContentAndCommitsDeletion()
    {
        var zone = await CreateZoneAsync("delete-success");

        using var response = await DeleteAsync(zone.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await ZoneExistsAsync(zone.Id));
    }

    [Fact]
    public async Task Delete_RejectsGeneratedZoneAndKeepsIt()
    {
        var zone = TenantDomainZoneFactory.CreateGenerated(
            TestData.TenantId,
            TestData.DefaultDeploymentTargetId,
            $"{TenantDomainZoneLabelPolicy.GeneratedPrefix}delete",
            DnsName.Create("apps.test.local"),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
        await Fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .InsertOneAsync(zone);

        using var response = await DeleteAsync(zone.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertDomainProblemAsync(
            response,
            "tag:pole.dev,2026:problem:domains.zone.delete.generated",
            "domains.zone.delete.generated",
            problem => Assert.Equal(
                zone.Id,
                problem.GetProperty("domainZoneId").GetString()));
        Assert.True(await ZoneExistsAsync(zone.Id));
    }

    [Fact]
    public async Task Delete_RejectsZoneWithHostnameBindingsAndKeepsIt()
    {
        var zone = await CreateZoneAsync("delete-bound");
        var binding = HostnameBindingFactory.CreateCustom(
            TestData.TenantId,
            zone.Id,
            TestData.DefaultDeploymentTargetId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "api",
            DnsName.Create(zone.Domain),
            new HostnameBindingFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
        await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .InsertOneAsync(binding);

        using var response = await DeleteAsync(zone.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertDomainProblemAsync(
            response,
            "tag:pole.dev,2026:problem:domains.zone.delete.has-hostname-bindings",
            "domains.zone.delete.has-hostname-bindings",
            problem =>
            {
                Assert.Equal(
                    zone.Id,
                    problem.GetProperty("domainZoneId").GetString());
                Assert.Equal(1, problem.GetProperty("bindingCount").GetInt64());
            });
        Assert.True(await ZoneExistsAsync(zone.Id));
    }

    [Fact]
    public async Task Delete_AbortsDeletionWhenReferenceRemovalIsRejected()
    {
        var zone = await CreateZoneAsync("delete-reference");
        RejectDeploymentTargetReferenceRemoval();

        using var response = await DeleteAsync(zone.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertDomainProblemAsync(
            response,
            "tag:pole.dev,2026:problem:domains.zone.delete.reference-removal-rejected",
            "domains.zone.delete.reference-removal-rejected",
            problem =>
            {
                Assert.Equal(
                    zone.Id,
                    problem.GetProperty("domainZoneId").GetString());
                Assert.Equal(
                    TestData.DefaultDeploymentTargetId,
                    problem.GetProperty("deploymentTargetId").GetString());
            });
        Assert.True(await ZoneExistsAsync(zone.Id));
    }

    [Fact]
    public async Task Delete_ReturnsNotFoundWithoutDeletingAnotherTenantZone()
    {
        var foreignZone = TenantDomainZoneFactory.CreateManagedReservation(
            OtherTenantId,
            TestData.DefaultDeploymentTargetId,
            "foreign-delete",
            DnsName.Create("apps.test.local"),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow));
        await Fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .InsertOneAsync(foreignZone);

        using var response = await DeleteAsync(foreignZone.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDomainProblemAsync(
            response,
            "tag:pole.dev,2026:problem:domains.zone.not-found",
            "domains.zone.not-found",
            problem => Assert.Equal(
                foreignZone.Id,
                problem.GetProperty("domainZoneId").GetString()));
        Assert.True(await ZoneExistsAsync(foreignZone.Id));
    }

    [Fact]
    public async Task Delete_AuthorizationPipelineShortCircuitsWithoutDeleting()
    {
        var zone = await CreateZoneAsync("delete-forbidden");
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        using var response = await DeleteAsync(zone.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ZoneExistsAsync(zone.Id));
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
            "tag:pole.dev,2026:problem:deployment-targets.not-found",
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
            "tag:pole.dev,2026:problem:deployment-targets.new-workloads-disabled",
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
            "tag:pole.dev,2026:problem:domains.zone.already-reserved",
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
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "tag:pole.dev,2026:problem:request.validation.failed",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "request.validation.failed",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            (int)HttpStatusCode.BadRequest,
            problem.RootElement.GetProperty("status").GetInt32());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Body.DeploymentTargetId", out _));
        Assert.True(errors.TryGetProperty("Body.Label", out _));
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
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "tag:pole.dev,2026:problem:tenant.authorization.role-required",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "tenant.authorization.role-required",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            (int)HttpStatusCode.Forbidden,
            problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "member",
            problem.RootElement.GetProperty("minimumRole").GetString());
        Assert.Equal(0L, await ZoneCountAsync());
    }

    private Task<HttpResponseMessage> CreateAsync(
        string deploymentTargetId,
        string label) =>
        Client.PostAsJsonAsync(
            "/api/v1/domain-zones/",
            new { deploymentTargetId, label });

    private async Task<TenantDomainZoneSnapshot> CreateZoneAsync(string label)
    {
        using var response = await CreateAsync(
            TestData.DefaultDeploymentTargetId,
            label);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var zone = await response.Content
            .ReadFromJsonAsync<TenantDomainZoneSnapshot>();
        Assert.NotNull(zone);
        return zone;
    }

    private Task<HttpResponseMessage> DeleteAsync(string zoneId) =>
        Client.DeleteAsync($"/api/v1/domain-zones/{zoneId}");

    private async Task<bool> ZoneExistsAsync(string zoneId) =>
        await Fixture.Database
            .GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(zone => zone.Id == zoneId) == 1;

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

    private static async Task AssertDomainProblemAsync(
        HttpResponseMessage response,
        string type,
        string code,
        Action<JsonElement>? assertExtensions = null)
    {
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(type, problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            (int)response.StatusCode,
            problem.RootElement.GetProperty("status").GetInt32());
        assertExtensions?.Invoke(problem.RootElement);
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
