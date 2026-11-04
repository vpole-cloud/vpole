using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
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
            tags = Array.Empty<string>(),
            placement = new
            {
                isolationScopeId = (string?)null,
                targets = new[]
                {
                    new
                    {
                        deploymentTargetId = TestData.DefaultDeploymentTargetId,
                        fixedReplicas = 2,
                        computeProfileId = (string?)null
                    }
                }
            },
            spec = new
            {
                image = spec.Image,
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

        var webService = await Fixture.Database.WebServices()
            .Find(service => service.Id == created.WebServiceId)
            .SingleAsync();
        var release = await Fixture.Database.WebServiceReleases()
            .Find(item => item.WebServiceId == created.WebServiceId && item.ResourceRevision == 1)
            .SingleAsync();

        Assert.NotNull(webService.Spec.Config.EnvironmentVariables);
        Assert.Empty(webService.Spec.Config.EnvironmentVariables);
        Assert.NotNull(release.Spec.Config.EnvironmentVariables);
        Assert.Empty(release.Spec.Config.EnvironmentVariables);
    }

    [Fact]
    public async Task AddDeployment_ReturnsTypedQuotaProblem_AndAbortsTheMutation()
    {
        await Fixture.Database
            .GetCollection<TenantQuotaPolicy>("TenantQuotaPolicies")
            .InsertOneAsync(
            QuotaDomainFactory.CreatePolicy(
                TestData.TenantId,
                QuotaDelta.From(CpuQuota.QuotaKey, 500),
                DateTime.UtcNow));
        var secondTargetId = ObjectId.GenerateNewId().ToString();
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("typed-quota-add").Body);
        var group = await groupResponse.Content
            .ReadFromJsonAsync<ResourceGroupCreatedResponse>(JsonOptions);
        Assert.NotNull(group);
        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/web-services/",
            TestData.CreateWebServiceRequest(group.Id, "typed-quota-add").Body);
        Assert.True(
            createResponse.StatusCode == HttpStatusCode.Accepted,
            await createResponse.Content.ReadAsStringAsync());
        var created = await createResponse.Content
            .ReadFromJsonAsync<CreateWebServiceResponse>(JsonOptions);
        Assert.NotNull(created);
        AddEligibleDeploymentTarget(
            secondTargetId,
            "apps.second.test.local");
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles")
            .ReplaceOneAsync(
                profile => profile.Id == TestData.DefaultComputeProfileId,
                TestData.SeedComputeProfile(deploymentTargetIds:
                    [TestData.DefaultDeploymentTargetId, secondTargetId]));

        var addResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments",
            new
            {
                deploymentTargetId = secondTargetId,
                fixedReplicas = 1,
                computeProfileId = (string?)null
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, addResponse.StatusCode);
        using var problem = JsonDocument.Parse(
            await addResponse.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        Assert.Equal(
            "quotas.reservation.exceeded",
            root.GetProperty("code").GetString());
        Assert.Equal("CpuMillis", root.GetProperty("quota").GetString());
        Assert.Equal(500, root.GetProperty("limit").GetInt64());
        Assert.Single(await Fixture.DbContext.ResourceDeployments
            .Find(deployment => deployment.ResourceId == created.WebServiceId)
            .ToListAsync());
        Assert.Single(await Fixture.Database.WebServiceReleases()
            .Find(release => release.WebServiceId == created.WebServiceId)
            .ToListAsync());
        Assert.Empty(await Fixture.Database
            .GetCollection<HostnameBinding>("HostnameBindings")
            .Find(binding =>
                binding.ResourceId == created.WebServiceId &&
                binding.DeploymentTargetId == secondTargetId)
            .ToListAsync());
    }

    [Fact]
    public async Task Viewer_CanReadHistory_ButCannotRedeployOrRollback()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        var historyResponse = await Client.GetAsync($"/api/v1/web-services/{webService.Id}/releases");
        var redeployResponse = await Client.PostAsync($"/api/v1/web-services/{webService.Id}/redeploy?expectedRevision={webService.ActiveRevision}", null);
        var rollbackResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{webService.Id}/rollback",
            new { targetRevision = 1, expectedRevision = webService.ActiveRevision });

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, redeployResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rollbackResponse.StatusCode);
    }

    [Fact]
    public async Task Member_CanRedeploy()
    {
        var webService = await InsertWebServiceAsync();
        await SetCurrentUserRoleAsync(TenantRole.Member);

        var response = await Client.PostAsync($"/api/v1/web-services/{webService.Id}/redeploy?expectedRevision={webService.ActiveRevision}", null);

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
        var saved = await Fixture.Database.WebServices()
            .Find(service => service.Id == webService.Id)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);

        var release = await Fixture.Database.WebServiceReleases()
            .Find(r => r.WebServiceId == webService.Id && r.ResourceRevision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Redeploy, release.Action);
        Assert.Equal(TestData.UserId, release.ActorUserId);
        Assert.Equal(TestData.UserEmail, release.ActorEmail);
    }

    [Theory]
    [InlineData("redeploy")]
    [InlineData("rollback")]
    public async Task Recovery_rejects_stale_revision_without_mutation(string action)
    {
        var service = await InsertWebServiceAsync();
        await InsertReleaseAsync(service, 1, WebServiceReleaseOutcome.Succeeded);
        var before = await Fixture.DbContext.ResourceDeployments.Find(x => x.ResourceId == service.Id).SingleAsync();
        using var response = action == "redeploy"
            ? await Client.PostAsync($"/api/v1/web-services/{service.Id}/redeploy?expectedRevision=2", null)
            : await Client.PostAsJsonAsync($"/api/v1/web-services/{service.Id}/rollback", new { targetRevision = 1, expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await Fixture.DbContext.ResourceDeployments.Find(x => x.Id == before.Id).SingleAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(1, (await Fixture.Database.WebServices().Find(x => x.Id == service.Id).SingleAsync()).ActiveRevision);
        Assert.Single(await Fixture.Database.WebServiceReleases().Find(x => x.WebServiceId == service.Id).ToListAsync());
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-updated"));
    }

    [Theory]
    [InlineData("redeploy")]
    [InlineData("rollback")]
    public async Task Recovery_requires_confirmation_revision(string action)
    {
        var service = await InsertWebServiceAsync();
        using var response = action == "redeploy"
            ? await Client.PostAsync($"/api/v1/web-services/{service.Id}/redeploy", null)
            : await Client.PostAsJsonAsync($"/api/v1/web-services/{service.Id}/rollback", new { targetRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await Fixture.Database.WebServices().Find(x => x.Id == service.Id).SingleAsync()).ActiveRevision);
    }

    [Theory]
    [InlineData(WebServiceReleaseOutcome.Succeeded, 1, HttpStatusCode.Conflict)]
    [InlineData(WebServiceReleaseOutcome.Succeeded, 2, HttpStatusCode.Conflict)]
    [InlineData(WebServiceReleaseOutcome.Pending, 1, HttpStatusCode.NotFound)]
    [InlineData(WebServiceReleaseOutcome.Failed, 1, HttpStatusCode.NotFound)]
    public async Task Rollback_requires_a_succeeded_prior_revision(WebServiceReleaseOutcome outcome, long revision, HttpStatusCode expected)
    {
        var service = await InsertWebServiceAsync();
        await InsertReleaseAsync(service, revision, outcome);
        using var response = await Client.PostAsJsonAsync($"/api/v1/web-services/{service.Id}/rollback", new { targetRevision = revision, expectedRevision = 1 });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, (await Fixture.Database.WebServices().Find(x => x.Id == service.Id).SingleAsync()).ActiveRevision);
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-updated"));
    }

    [Fact]
    public async Task Same_spec_rollback_after_redeploy_creates_a_new_release_and_generation()
    {
        var service = await InsertWebServiceAsync();
        await InsertReleaseAsync(service, 1, WebServiceReleaseOutcome.Succeeded);
        using var redeployed = await Client.PostAsync($"/api/v1/web-services/{service.Id}/redeploy?expectedRevision=1", null);
        Assert.Equal(HttpStatusCode.Accepted, redeployed.StatusCode);
        using var response = await Client.PostAsJsonAsync($"/api/v1/web-services/{service.Id}/rollback", new { targetRevision = 1, expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var saved = await Fixture.Database.WebServices().Find(x => x.Id == service.Id).SingleAsync();
        Assert.Equal(3, saved.ActiveRevision);
        Assert.Equal(service.Spec.Image.ResolvedImage!.Tag, saved.Spec.Image.ResolvedImage!.Tag);
        Assert.Equal(service.Exposure, saved.Exposure);
        var release = await Fixture.Database.WebServiceReleases().Find(x => x.WebServiceId == service.Id && x.ResourceRevision == 3).SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Rollback, release.Action);
        Assert.Equal(WebServiceReleaseOutcome.Pending, release.Outcome);
        Assert.NotNull(release.CorrelationId);
        Assert.NotNull(release.TraceId);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(x => x.ResourceId == service.Id).SingleAsync();
        Assert.Equal(3, placement.ResourceRevision);
        Assert.Equal(3, placement.DesiredGeneration);
        // The domain operation must also advance when the snapshot is the very same object.
        var repeated = saved.Rollback(saved.Spec);
        Assert.Equal(4, repeated.ActiveRevision);
        Assert.Equal(saved.Revision + 1, repeated.Revision);
    }

    [Fact]
    public async Task History_pages_owned_releases_in_revision_order_and_rejects_unbounded_skip()
    {
        var service = await InsertWebServiceAsync();
        for (var revision = 1; revision <= 3; revision++) await InsertReleaseAsync(service, revision, WebServiceReleaseOutcome.Succeeded);
        var foreign = service with { TenantId = ObjectId.GenerateNewId().ToString() };
        await InsertReleaseAsync(foreign, 4, WebServiceReleaseOutcome.Succeeded);
        var history = (await Client.GetFromJsonAsync<WebServiceReleaseHistoryDto>($"/api/v1/web-services/{service.Id}/releases?pageNumber=2&pageSize=1"))!;
        Assert.Equal(3, history.Releases.TotalCount);
        Assert.Equal(2, Assert.Single(history.Releases.Items).ResourceRevision);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/api/v1/web-services/{service.Id}/releases?pageNumber=2147483647&pageSize=100")).StatusCode);
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

        var savedToken = await Fixture.Database.WebServiceDeploymentTokens()
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
        Assert.Equal(2, response.ResourceRevision);
        Assert.Equal("2026.07.09-abc1234", response.Tag);

        var saved = await Fixture.Database.WebServices()
            .Find(service => service.Id == webService.Id)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);
        Assert.Equal("2026.07.09-abc1234", saved.Spec.Image.ResolvedImage!.Tag);

        var release = await Fixture.Database.WebServiceReleases()
            .Find(r => r.WebServiceId == webService.Id && r.ResourceRevision == 2)
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

        var usedToken = await Fixture.Database.WebServiceDeploymentTokens()
            .Find(token => token.Id == createdToken.Token.Id)
            .SingleAsync();
        Assert.NotNull(usedToken.LastUsedAtUtc);
        Assert.Equal("github-actions", usedToken.LastUsedSource);
        Assert.Equal(1L, await Fixture.DbContext.CountOutboxMessagesAsync("pole.web-services/v1/web-service-updated"));
        Assert.Equal(1L, await Fixture.DbContext.CountOutboxMessagesAsync(
            "pole.resource-deployments/v1/resource-deployment-reconciliation-requested"));
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

    [Fact]
    public async Task DeploymentHostnameRoutes_AddListAndDeleteOnlyCustomBinding()
    {
        var resourceGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("hostname-api").Body);
        var resourceGroup = await resourceGroupResponse.Content
            .ReadFromJsonAsync<ResourceGroupCreatedResponse>(JsonOptions);
        Assert.NotNull(resourceGroup);
        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/web-services/",
            TestData.CreateWebServiceRequest(resourceGroup.Id, "hostname-api").Body);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var created = await createResponse.Content
            .ReadFromJsonAsync<CreateWebServiceResponse>(JsonOptions);
        Assert.NotNull(created);
        var deployment = Assert.Single(created.Deployments);

        var generatedResponse = await Client.GetAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames");
        Assert.Equal(HttpStatusCode.OK, generatedResponse.StatusCode);
        var generated = Assert.Single((await generatedResponse.Content
            .ReadFromJsonAsync<List<HostnameBindingSnapshot>>(JsonOptions))!);
        Assert.Equal(HostnameBindingKinds.Generated, generated.Kind);

        var zoneResponse = await Client.PostAsJsonAsync(
            "/api/v1/domain-zones/",
            new
            {
                technicalName = "company-zone",
                deploymentTargetId = deployment.DeploymentTargetId,
                label = "company"
            });
        Assert.Equal(HttpStatusCode.Created, zoneResponse.StatusCode);
        var zone = await zoneResponse.Content
            .ReadFromJsonAsync<TenantDomainZoneSnapshot>(JsonOptions);
        Assert.NotNull(zone);
        var addResponse = await Client.PostAsJsonAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames",
            new { technicalName = "api-hostname", tenantDomainZoneId = zone.Id, relativeName = "api" });
        Assert.Equal(HttpStatusCode.Accepted, addResponse.StatusCode);

        var listResponse = await Client.GetAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames");
        var bindings = (await listResponse.Content
            .ReadFromJsonAsync<List<HostnameBindingSnapshot>>(JsonOptions))!;
        Assert.Equal(2, bindings.Count);
        var custom = Assert.Single(bindings, binding =>
            binding.Kind == HostnameBindingKinds.Custom);
        Assert.Equal("api.company.apps.test.local", custom.Hostname);

        var generatedDelete = await Client.DeleteAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames/{generated.Id}");
        Assert.Equal(HttpStatusCode.Conflict, generatedDelete.StatusCode);
        var customDelete = await Client.DeleteAsync(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames/{custom.Id}");
        Assert.Equal(HttpStatusCode.Accepted, customDelete.StatusCode);
        var finalBindings = await Client.GetFromJsonAsync<List<HostnameBindingSnapshot>>(
            $"/api/v1/web-services/{created.WebServiceId}/deployments/{deployment.DeploymentId}/hostnames",
            JsonOptions);
        Assert.Single(finalBindings!);
        Assert.Equal(HostnameBindingKinds.Generated, finalBindings![0].Kind);
    }

    private async Task InsertReleaseAsync(WebService service, long revision, WebServiceReleaseOutcome outcome)
    {
        var placement = await Fixture.DbContext.ResourceDeployments.Find(x => x.ResourceId == service.Id).SingleAsync();
        var release = WebServiceReleaseFactory.CreatePending(service.TenantId, service.Id, revision, WebServiceReleaseAction.Update,
            TestData.UserId, TestData.UserEmail, service.Spec,
            [new WebServiceReleaseDeployment(placement.Id, placement.ResolvedPlacement.DeploymentTargetId, revision, placement.DesiredGeneration,
                WebServiceReleaseDeploymentOperation.Apply, WebServiceReleaseDeploymentOutcome.Succeeded, DateTime.UtcNow, null, null)],
            null, null, new WebServiceReleaseFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow), service.Exposure)
            with
        { Outcome = outcome, CompletedAtUtc = outcome == WebServiceReleaseOutcome.Pending ? null : DateTime.UtcNow };
        await Fixture.Database.WebServiceReleases().InsertOneAsync(release);
    }

    private async Task<WebService> InsertWebServiceAsync()
    {
        var resourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var resourceGroupId = resourceGroup.Id;
        var isolationScopeId = resourceGroup.DefaultIsolationScopeId;
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
                compute: ResourceDeploymentTestData.Compute(),
                resolvedAtUtc: createdAtUtc.AddSeconds(1)),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc = createdAtUtc.AddSeconds(1)
            });
        await Fixture.DbContext.RunOutcomeInTransactionAsync<Unit>(
            async (transaction, cancellationToken) =>
            {
                await Fixture.Database.WebServices().InsertOneAsync(
                    transaction.Session,
                    webService,
                    cancellationToken: cancellationToken);
                await Fixture.DbContext.ResourceRegistrations.InsertOneAsync(
                    transaction.Session,
                    ResourceRegistrationFactory.Create(
                        webService.TenantId,
                        resourceGroupId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        webService.Name,
                        owningPluginId: null,
                        new ResourceRegistrationFactory.CreateOptions(
                            ObjectId.GenerateNewId().ToString(),
                            createdAtUtc)),
                    cancellationToken: cancellationToken);
                await Fixture.DbContext.RuntimeCells.InsertOneAsync(
                    transaction.Session,
                    runtimeCell,
                    cancellationToken: cancellationToken);
                await Fixture.DbContext.ResourceDeployments.InsertOneAsync(
                    transaction.Session,
                    deployment,
                    cancellationToken: cancellationToken);
                return transaction.Commit(Unit.Value);
            });
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
        long ResourceRevision,
        string ReleaseId,
        string Tag);

    private sealed record ResourceGroupCreatedResponse(
        string Id,
        string Name,
        string Description,
        IReadOnlyCollection<string> Tags);
}
