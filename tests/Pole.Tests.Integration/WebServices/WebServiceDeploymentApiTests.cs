using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceDeploymentApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Create_NormalizesAnEmptyEnvironmentVariablesObject_ForTheServiceAndInitialRelease()
    {
        var resourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var spec = TestData.ValidWebServiceSpec();

        using var response = await Client.PostAsJsonAsync("/api/v1/web-services", new
        {
            name = "empty-environment",
            resourceGroupId = resourceGroup.Id,
            domainName = "empty-environment.example.test",
            tags = Array.Empty<string>(),
            spec = new
            {
                image = spec.Image,
                spec.Replicas,
                spec.Resources,
                config = new
                {
                    environmentVariables = new Dictionary<string, string>(),
                    portMappings = spec.Config.PortMappings
                },
                spec.Autoscaling,
                spec.Probes
            }
        });

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<CreateWebServiceResponse>(JsonOptions);
        Assert.NotNull(created);

        var webService = await Fixture.DbContext.WebServices
            .Find(service => service.Id == created.WebServiceId)
            .SingleAsync();
        var release = await Fixture.DbContext.WebServiceReleases
            .Find(item => item.WebServiceId == created.WebServiceId && item.Revision == 1)
            .SingleAsync();

        Assert.NotNull(webService.Spec.Config.EnvironmentVariables);
        Assert.Empty(webService.Spec.Config.EnvironmentVariables);
        Assert.NotNull(release.Spec.Config.EnvironmentVariables);
        Assert.Empty(release.Spec.Config.EnvironmentVariables);
    }

    [Fact]
    public async Task Viewer_CanReadHistory_ButCannotRedeployOrRollback()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        var historyResponse = await Client.GetAsync($"/api/v1/web-services/{webService.Id}/deployments");
        var redeployResponse = await Client.PostAsync($"/api/v1/web-services/{webService.Id}/redeploy", null);
        var rollbackResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/rollback",
            new { targetRevision = 1 });

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, redeployResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rollbackResponse.StatusCode);
    }

    [Fact]
    public async Task Member_CanRedeploy()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Member);

        var response = await Client.PostAsync($"/api/v1/web-services/{webService.Id}/redeploy", null);

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
        var saved = await Fixture.DbContext.WebServices
            .Find(service => service.Id == webService.Id)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);

        var release = await Fixture.DbContext.WebServiceReleases
            .Find(r => r.WebServiceId == webService.Id && r.Revision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Redeploy, release.Action);
        Assert.Equal(TestData.UserId, release.ActorUserId);
        Assert.Equal(TestData.UserEmail, release.ActorEmail);
    }

    [Fact]
    public async Task Member_CanCreateDeploymentToken_AndTokenCanUpdateImageTag()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Member);

        var tokenResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/deployment-tokens",
            new { name = "github-actions" });

        Assert.Equal(HttpStatusCode.Created, tokenResponse.StatusCode);
        var createdToken = await tokenResponse.Content.ReadFromJsonAsync<CreateDeploymentTokenResponse>(JsonOptions);
        Assert.NotNull(createdToken);
        Assert.StartsWith(WebServiceDeploymentTokenFactory.RawTokenPrefix, createdToken.RawToken);
        Assert.Equal("github-actions", createdToken.Token.Name);
        Assert.Contains(WebServiceDeploymentTokenOperations.UpdateImageTag, createdToken.Token.AllowedOperations);

        var savedToken = await Fixture.DbContext.WebServiceDeploymentTokens
            .Find(token => token.Id == createdToken.Token.Id)
            .SingleAsync();
        Assert.Equal(WebServiceDeploymentTokenFactory.Hash(createdToken.RawToken), savedToken.TokenHash);
        Assert.NotEqual(createdToken.RawToken, savedToken.TokenHash);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/web-services/{webService.Id}/image-tag")
        {
            Content = JsonContent.Create(new
            {
                tag = "2026.07.09-abc1234",
                source = "github-actions",
                commitSha = "abc1234",
                buildUrl = "https://github.com/lsbV/pole-dotnet-sample/actions/runs/1"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", createdToken.RawToken);

        var updateResponse = await Client.SendAsync(request);

        Assert.True(
            updateResponse.StatusCode == HttpStatusCode.Accepted,
            await updateResponse.Content.ReadAsStringAsync());
        var response = await updateResponse.Content.ReadFromJsonAsync<UpdateImageTagResponse>(JsonOptions);
        Assert.NotNull(response);
        Assert.Equal(2, response.Revision);
        Assert.Equal("2026.07.09-abc1234", response.Tag);

        var saved = await Fixture.DbContext.WebServices
            .Find(service => service.Id == webService.Id)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);
        Assert.Equal("2026.07.09-abc1234", saved.Spec.Image.Tag);

        var release = await Fixture.DbContext.WebServiceReleases
            .Find(r => r.WebServiceId == webService.Id && r.Revision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.ImageTagUpdate, release.Action);
        Assert.Equal(WebServiceReleaseOutcome.Pending, release.Outcome);
        Assert.Null(release.ActorUserId);
        Assert.Null(release.ActorEmail);
        var provenance = Assert.IsType<ExternalImageTagUpdateProvenance>(release.Provenance);
        Assert.Equal("github-actions", provenance.Source);
        Assert.Equal("abc1234", provenance.CommitSha);
        Assert.Equal("1.27", provenance.PreviousImageTag);
        Assert.Equal("2026.07.09-abc1234", provenance.NewImageTag);
        Assert.Equal("DeploymentToken", provenance.ActorType);
        Assert.Equal(createdToken.Token.Id, provenance.DeploymentTokenId);
        Assert.Equal("github-actions", provenance.DeploymentTokenName);

        var usedToken = await Fixture.DbContext.WebServiceDeploymentTokens
            .Find(token => token.Id == createdToken.Token.Id)
            .SingleAsync();
        Assert.NotNull(usedToken.LastUsedAtUtc);
        Assert.Equal("github-actions", usedToken.LastUsedSource);
        Assert.Equal(1L, await Fixture.DbContext.CountOutboxMessagesAsync("web-service-updated"));
        Assert.Equal(1L, await Fixture.DbContext.CountOutboxMessagesAsync(
            "resource-deployment-reconciliation-requested"));
    }

    [Fact]
    public async Task Viewer_CannotCreateDeploymentToken_OrUpdateImageTagWithUserToken()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        var tokenResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/deployment-tokens",
            new { name = "viewer-token" });
        var updateResponse = await Client.PatchAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/image-tag",
            new { tag = "2026.07.09-viewer", source = "manual" });

        Assert.Equal(HttpStatusCode.Forbidden, tokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task DeploymentTokenRejectsTenantHeaderMismatch()
    {
        var webService = await InsertWebServiceAsync();
        var tokenResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/deployment-tokens",
            new { name = "github-actions" });
        var createdToken = await tokenResponse.Content.ReadFromJsonAsync<CreateDeploymentTokenResponse>(JsonOptions);
        Assert.NotNull(createdToken);

        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/web-services/{webService.Id}/image-tag")
        {
            Content = JsonContent.Create(new { tag = "2026.07.09-mismatch", source = "github-actions" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", createdToken.RawToken);
        request.Headers.Add(TenantHeaders.TenantId, "507f1f77bcf86cd799439022");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokedDeploymentTokenCannotUpdateImageTag()
    {
        var webService = await InsertWebServiceAsync();
        var tokenResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/deployment-tokens",
            new { name = "github-actions" });
        var createdToken = await tokenResponse.Content.ReadFromJsonAsync<CreateDeploymentTokenResponse>(JsonOptions);
        Assert.NotNull(createdToken);

        var revokeResponse = await Client.DeleteAsync(
            $"/api/v1/web-services/{webService.Id}/deployment-tokens/{createdToken.Token.Id}");
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/web-services/{webService.Id}/image-tag")
        {
            Content = JsonContent.Create(new { tag = "2026.07.09-revoked", source = "github-actions" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", createdToken.RawToken);

        var updateResponse = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, updateResponse.StatusCode);
    }

    private async Task<WebService> InsertWebServiceAsync()
    {
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var isolationScopeId = ObjectId.GenerateNewId().ToString();
        var runtimeCellId = ObjectId.GenerateNewId().ToString();
        var createdAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var webService = TestData.SeedWebService(resourceGroupId);
        var runtimeCell = RuntimeCellFactory.Create(
                TestData.TenantId,
                resourceGroupId,
                isolationScopeId,
                TestData.DefaultDeploymentTargetId,
                "test-api-deployment",
                new RuntimeCellFactory.CreateOptions(
                    runtimeCellId,
                    createdAtUtc))
            .MarkReady(
                expectedGeneration: 1,
                createdAtUtc.AddSeconds(1));
        var deployment = ResourceDeploymentFactory.Create(
            TestData.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            resourceGroupId,
            isolationScopeId,
            ResolvedResourcePlacement.Create(
                runtimeCell.Id,
                runtimeCell.DeploymentTargetId,
                runtimeCell.KubernetesNamespace,
                runtimeCellGeneration: runtimeCell.DesiredGeneration,
                targetWorkloadConfigurationRevision: 1,
                ResolvedIngressConfiguration.Create(
                    "traefik",
                    "letsencrypt-prod"),
                storage: null,
                createdAtUtc.AddSeconds(1)),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                createdAtUtc.AddSeconds(1)));
        await Fixture.DbContext.WebServices.InsertOneAsync(webService);
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
        return webService;
    }

    private async Task SetCurrentUserRoleAsync(TenantRole role)
    {
        var tenant = await Fixture.DbContext.Tenants
            .Find(t => t.Id == TestData.TenantId)
            .SingleAsync();

        var updatedTenant = tenant with
        {
            Members = tenant.Members
                .Select(member => member.UserId == TestData.UserId ? member with { Role = role } : member)
                .ToImmutableList()
        };

        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            t => t.Id == TestData.TenantId,
            updatedTenant);
    }

    private sealed record CreateDeploymentTokenResponse(
        DeploymentTokenDto Token,
        string RawToken);

    private sealed record DeploymentTokenDto(
        string Id,
        string Name,
        string TokenPrefix,
        string[] AllowedOperations);

    private sealed record UpdateImageTagResponse(
        string Id,
        int Revision,
        string ReleaseId,
        string Tag);
}
