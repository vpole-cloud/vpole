using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Request;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Application.Endpoints;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ApiSliceTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task CreateUser_ReturnsCreatedForPlatformAdmin()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var response = await Client.PostAsJsonAsync(
            "/api/v1/users/",
            new CreateUserRequestBody("new-user@example.test", "New", "User"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(user);
        Assert.Equal("new-user@example.test", user.Email);
    }

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
    public async Task RetryResourceDeployment_ReturnsTypedNotFoundProblem()
    {
        const string missingDeploymentId = "507f1f77bcf86cd799439099";

        using var response = await Client.PostAsync(
            $"/api/v1/resource-deployments/{missingDeploymentId}/retry",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "tag:pole.dev,2026:problem:resource-deployments.not-found",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "resource-deployments.not-found",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            missingDeploymentId,
            problem.RootElement.GetProperty("deploymentId").GetString());
    }

    [Fact]
    public async Task GetWebServiceById_ReturnsNotFound_ForMissingResource()
    {
        const string missingId = "507f1f77bcf86cd799439013";

        var response = await Client.GetAsync($"/api/v1/web-services/{missingId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Web service not found.", payload.RootElement.GetProperty("message").GetString());
        Assert.Equal(missingId, payload.RootElement.GetProperty("id").GetString());
        Assert.False(payload.RootElement.TryGetProperty("entity", out _));
    }

    [Fact]
    public async Task CreateWebService_ReturnsGeneratedDeployment()
    {
        var resourceGroupResponse = await Client.PostAsJsonAsync("/api/v1/resource-groups/", TestData.CreateResourceGroupRequest("web-rg").Body);
        var resourceGroup = await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupCreatedResponse>();
        Assert.NotNull(resourceGroup);

        var response = await Client.PostAsJsonAsync(
            "/api/v1/web-services/",
            TestData.CreateWebServiceRequest(resourceGroup.Id, name: "generated-hostname-web").Body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateWebServiceResponse>();
        Assert.NotNull(created);
        Assert.Single(created.Deployments);
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

    [Fact]
    public async Task CreatePostgresCluster_ReturnsValidationProblem_ForMalformedResourceGroupId()
    {
        var valid = TestData.CreatePostgresClusterRequest(
            "507f1f77bcf86cd799439099",
            name: "invalid-postgres-reference").Body;
        var invalid = new
        {
            valid.Name,
            GroupId = "not-an-object-id",
            valid.Version,
            valid.Instances,
            valid.Storage,
            valid.StorageProfileId,
            valid.ComputeProfileId,
            valid.Username,
            valid.Password,
            valid.Placement
        };

        var response = await Client.PostAsJsonAsync("/api/v1/postgres/", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors")
            .TryGetProperty("Body.GroupId", out _));
    }

    [Fact]
    public async Task GetStorageProfiles_ReturnsForbidden_WithoutPlatformAdminRole()
    {
        var response = await Client.GetAsync("/api/v1/platform/storage/profiles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateStorageProfile_ReturnsCreated_ForPlatformAdmin()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var response = await Client.PostAsJsonAsync(
            "/api/v1/platform/storage/profiles",
            new CreateStorageProfileRequestBody(
                DisplayName: "Fast SSD",
                Description: "Fast tenant storage",
                MediaType: "SSD",
                PerformanceTier: "Fast",
                IsEnabled: true,
                IsDefault: false,
                TargetMappings:
                [
                    new StorageProfileTargetMappingRequestBody(
                        TestData.DefaultDeploymentTargetId,
                        "fast")
                ],
                AllowedResourceTypes: ["Redis", "Postgres"],
                TenantAccess: new StorageProfileTenantAccessDto("AllTenants", []),
                SizeLimits: new StorageProfileSizeLimitsDto(1, 100)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<StorageProfileDto>();
        Assert.NotNull(payload);
        Assert.Equal("Fast SSD", payload.DisplayName);
        Assert.Equal(
            "fast",
            Assert.Single(payload.TargetMappings).StorageClassName);
    }

    [Fact]
    public async Task GetComputeProfiles_RequiresPlatformAdmin()
    {
        var response = await Client.GetAsync(
            "/api/v1/platform/compute-profiles?pageNumber=1&pageSize=10");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetComputeProfiles_ReturnsCatalog_ForPlatformAdmin()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var response = await Client.GetAsync(
            "/api/v1/platform/compute-profiles?pageNumber=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ComputeProfilePageDto>();
        var profile = Assert.Single(page!.Items);
        Assert.True(profile.IsDefault);
        Assert.Equal("Default", profile.DisplayName);
    }

    [Fact]
    public async Task GetComputeProfile_ReturnsTypedNotFound_ForPlatformAdmin()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        const string computeProfileId = "507f1f77bcf86cd799439095";

        var response = await Client.GetAsync(
            $"/api/v1/platform/compute-profiles/{computeProfileId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("compute-profiles.not-found", problem.RootElement
            .GetProperty("code").GetString());
        Assert.Equal(computeProfileId, problem.RootElement
            .GetProperty("computeProfileId").GetString());
    }

    [Fact]
    public async Task CreateComputeProfile_MapsMissingSelectedTenantThroughTheTypedContract()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        const string missingTenantId = "507f1f77bcf86cd799439094";

        var response = await Client.PostAsJsonAsync(
            "/api/v1/platform/compute-profiles",
            CreateComputeProfileBody(
                new ComputeProfileTenantAccessRequestBody(
                    "selected-tenants",
                    [missingTenantId])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "compute-profiles.tenant-access.tenants-not-found",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            missingTenantId,
            problem.RootElement.GetProperty("tenantIds")[0].GetString());
    }

    [Fact]
    public async Task CreateComputeProfile_MapsRejectedTargetAsBodyValidation()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        RejectNewDeploymentTargetWorkloads();

        var response = await Client.PostAsJsonAsync(
            "/api/v1/platform/compute-profiles",
            CreateComputeProfileBody(
                new ComputeProfileTenantAccessRequestBody("all-tenants", [])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "compute-profiles.target-mappings.invalid",
            problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors")
            .TryGetProperty("TargetMappings[0].DeploymentTargetId", out _));
    }

    [Fact]
    public async Task DeleteComputeProfile_ReturnsTypedDefaultConflict_ForPlatformAdmin()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var response = await Client.DeleteAsync(
            $"/api/v1/platform/compute-profiles/{TestData.DefaultComputeProfileId}?revision=1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "compute-profiles.default-delete-forbidden",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            TestData.DefaultComputeProfileId,
            problem.RootElement.GetProperty("computeProfileId").GetString());
    }

    private static CreateComputeProfileRequestBody CreateComputeProfileBody(
        ComputeProfileTenantAccessRequestBody tenantAccess) =>
        new(
            "General purpose",
            "API test profile",
            new ComputeResourceRequirementsRequestBody(
                new ComputeResourceValuesRequestBody(250, 256),
                new ComputeResourceValuesRequestBody(1000, 1024)),
            "Any",
            [],
            ["pole/web-service"],
            tenantAccess,
            [new ComputeProfileTargetMappingRequestBody(
                TestData.DefaultDeploymentTargetId,
                [],
                [],
                [],
                [],
                null)],
            IsEnabled: true,
            IsDefault: false);

    [Fact]
    public async Task GetAvailableComputeProfiles_RequiresTenantHeader()
    {
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);

        var response = await Client.GetAsync(
            $"/api/v1/compute-profiles/available?deploymentTargetId={TestData.DefaultDeploymentTargetId}&resourceType=pole/redis");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailableComputeProfiles_ReturnsEligibleDefault()
    {
        var response = await Client.GetAsync(
            $"/api/v1/compute-profiles/available?deploymentTargetId={TestData.DefaultDeploymentTargetId}&resourceType=pole/redis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profiles = await response.Content.ReadFromJsonAsync<
            IReadOnlyCollection<AvailableComputeProfileDto>>();
        var profile = Assert.Single(profiles!);
        Assert.True(profile.IsDefault);
        Assert.Equal(250, profile.Resources.Requests.CpuMillis);
        Assert.Equal(256, profile.Resources.Requests.MemoryMi);
    }

    [Fact]
    public async Task PlatformAdminMustSelectTenantBeforeListingResources()
    {
        var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("platform-list").Body);
        var resourceGroup =
            await resourceGroupResponse.Content.ReadFromJsonAsync<ResourceGroupCreatedResponse>();
        Assert.NotNull(resourceGroup);

        var webCreate = await Client.PostAsJsonAsync(
            "/api/v1/web-services/",
            TestData.CreateWebServiceRequest(
                resourceGroup.Id,
                "platform-web").Body);
        var redisCreate = await Client.PostAsJsonAsync(
            "/api/v1/redis-services/",
            TestData.CreateRedisServiceRequest(
                resourceGroup.Id,
                "platform-redis").Body);
        var postgresCreate = await Client.PostAsJsonAsync(
            "/api/v1/postgres/",
            TestData.CreatePostgresClusterRequest(
                resourceGroup.Id,
                "platform-postgres").Body);
        Assert.Equal(HttpStatusCode.Accepted, webCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, redisCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, postgresCreate.StatusCode);

        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");

        var webResponse = await Client.GetAsync(
            "/api/v1/web-services/?pageNumber=1&pageSize=10");
        var redisResponse = await Client.GetAsync(
            "/api/v1/redis-services/?pageNumber=1&pageSize=10");
        var postgresResponse = await Client.GetAsync(
            "/api/v1/postgres/?pageNumber=1&pageSize=10");

        Assert.Equal(HttpStatusCode.BadRequest, webResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, redisResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, postgresResponse.StatusCode);

        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);

        webResponse = await Client.GetAsync(
            "/api/v1/web-services/?pageNumber=1&pageSize=10");
        redisResponse = await Client.GetAsync(
            "/api/v1/redis-services/?pageNumber=1&pageSize=10");
        postgresResponse = await Client.GetAsync(
            "/api/v1/postgres/?pageNumber=1&pageSize=10");
        Assert.True(
            webResponse.IsSuccessStatusCode,
            await webResponse.Content.ReadAsStringAsync());
        Assert.True(
            redisResponse.IsSuccessStatusCode,
            await redisResponse.Content.ReadAsStringAsync());
        Assert.True(
            postgresResponse.IsSuccessStatusCode,
            await postgresResponse.Content.ReadAsStringAsync());
        var web =
            await webResponse.Content.ReadFromJsonAsync<PaginatedResponse<WebServiceDto>>();
        var redis =
            await redisResponse.Content.ReadFromJsonAsync<PaginatedResponse<RedisDto>>();
        var postgres =
            await postgresResponse.Content.ReadFromJsonAsync<
                PaginatedResponse<PostgresClusterDto>>();

        var webItem = Assert.Single(web!.Items);
        var redisItem = Assert.Single(redis!.Items);
        var postgresItem = Assert.Single(postgres!.Items);
        Assert.NotEmpty(webItem.Deployments);
        Assert.NotNull(redisItem.Placement);
        Assert.NotNull(postgresItem.Placement);

        var webDetail = await Client.GetFromJsonAsync<WebServiceDto>(
            $"/api/v1/web-services/{webItem.Id}");
        var redisDetail = await Client.GetFromJsonAsync<RedisDto>(
            $"/api/v1/redis-services/{redisItem.Id}");
        var postgresDetail = await Client.GetFromJsonAsync<PostgresClusterDto>(
            $"/api/v1/postgres/{postgresItem.Id}");
        Assert.NotEmpty(webDetail!.Deployments);
        Assert.NotNull(redisDetail!.Placement);
        Assert.NotNull(postgresDetail!.Placement);
    }

    private sealed record ResourceGroupCreatedResponse(string Id, string Name, string Description, IReadOnlyCollection<string> Tags);
    private sealed record SupportedPostgresVersionsResponse(int From, int To);
}
