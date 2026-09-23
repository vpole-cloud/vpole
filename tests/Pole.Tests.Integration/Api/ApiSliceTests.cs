using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.Core.Multitenancy;
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
    public async Task MemberTargetUsesRouteMembershipIndependentlyOfTheActiveHeader()
    {
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, "malformed-active-selection");
        var response = await Client.GetAsync($"/api/v1/tenants/{TestData.TenantId}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var forbidden = await Client.GetAsync("/api/v1/tenants/507f1f77bcf86cd799439099/members");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

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
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "tag:pole.dev,2026:problem:web-services.not-found",
            payload.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "Web service not found",
            payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "web-services.not-found",
            payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            missingId,
            payload.RootElement.GetProperty("webServiceId").GetString());
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
                AllowedResourceTypes: ["third-party/widget", "pole/postgresql"],
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
            $"/api/v1/compute-profiles/available?deploymentTargetId={TestData.DefaultDeploymentTargetId}&resourceType=third-party/widget");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailableComputeProfiles_ReturnsEligibleDefault()
    {
        var response = await Client.GetAsync(
            $"/api/v1/compute-profiles/available?deploymentTargetId={TestData.DefaultDeploymentTargetId}&resourceType=third-party/widget");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profiles = await response.Content.ReadFromJsonAsync<
            IReadOnlyCollection<AvailableComputeProfileDto>>();
        var profile = Assert.Single(profiles!);
        Assert.True(profile.IsDefault);
        Assert.Equal(250, profile.Resources.Requests.CpuMillis);
        Assert.Equal(256, profile.Resources.Requests.MemoryMi);
    }
    private sealed record ResourceGroupCreatedResponse(
        string Id,
        string Name,
        string Description,
        IReadOnlyCollection<string> Tags);

}
