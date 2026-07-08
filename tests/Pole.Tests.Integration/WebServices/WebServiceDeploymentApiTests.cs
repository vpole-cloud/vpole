using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceDeploymentApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
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

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
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

    private async Task<WebService> InsertWebServiceAsync()
    {
        var webService = TestData.SeedWebService("resource-group-api");
        await Fixture.DbContext.WebServices.InsertOneAsync(webService);
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
}
