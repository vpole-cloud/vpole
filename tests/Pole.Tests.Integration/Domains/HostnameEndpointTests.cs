using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class HostnameEndpointTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task AwaitingArtifactHostnameAddAndDeleteKeepPlacementPreparedWithoutRelease()
    {
        var (zone, created) = await CreateOwnerAsync(WebServiceCreationMode.SourceBuild);
        var services = Fixture.Database.GetCollection<WebService>("WebServices");
        var current = await services.Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.IsType<PreparedWebServiceLifecycle>(current.Lifecycle);
        var deployment = Assert.Single(created.Deployments);
        using var preparedRejected = await AddAsync(zone.Id, created.WebServiceId, deployment.DeploymentId, "api");
        Assert.Equal(HttpStatusCode.Conflict, preparedRejected.StatusCode);

        // Seed the accepted owner configuration; artifact promotion is intentionally absent from this endpoint test.
        var accepted = current.AcceptConfigurationAwaitingArtifact(
            current.Spec with { Image = new PendingSourceBuildImage(ObjectId.GenerateNewId().ToString()) },
            current.Tags, current.Exposure);
        await services.ReplaceOneAsync(item => item.Id == current.Id, accepted);
        var placements = Fixture.DbContext.ResourceDeployments;
        var original = await placements.Find(item => item.Id == deployment.DeploymentId).SingleAsync();
        await placements.ReplaceOneAsync(item => item.Id == original.Id,
            original.AdvanceResourceRevision(accepted.ActiveRevision, DateTime.UtcNow));
        var releases = Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases");
        Assert.Equal(0, await releases.CountDocumentsAsync(item => item.WebServiceId == current.Id));

        using var added = await AddAsync(zone.Id, current.Id, deployment.DeploymentId, "@");
        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        var summary = (await added.Content.ReadFromJsonAsync<PlacementSummaryDto>())!;
        Assert.Equal(ResourceDeploymentStatus.Prepared, summary.Status);
        Assert.Contains(zone.Domain, summary.Ingress!.Hostnames);
        var physical = await placements.Find(item => item.Id == original.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(physical.State);
        Assert.Equal(0, await RuntimeRequestsAsync());
        Assert.Equal(0, await releases.CountDocumentsAsync(item => item.WebServiceId == current.Id));
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(
            (await services.Find(item => item.Id == current.Id).SingleAsync()).Lifecycle);

        var bindings = (await Client.GetFromJsonAsync<WebServiceHostnameBindingDto[]>(
            $"/api/v1/web-services/{current.Id}/deployments/{deployment.DeploymentId}/hostnames"))!;
        var custom = Assert.Single(bindings, item => item.TechnicalName == "custom-hostname");
        Assert.Null(custom.GitOps);
        using var deleted = await Client.DeleteAsync(
            $"/api/v1/web-services/{current.Id}/deployments/{deployment.DeploymentId}/hostnames/{custom.Id}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        var removed = await placements.Find(item => item.Id == original.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(removed.State);
        Assert.DoesNotContain(zone.Domain, removed.ResolvedPlacement.Ingress!.Hostnames);
        Assert.Equal(0, await RuntimeRequestsAsync());
        Assert.Equal(0, await releases.CountDocumentsAsync(item => item.WebServiceId == current.Id));
    }

    [Fact]
    public async Task HostnameLongerThanDnsLimitReturnsTypedBadRequestWithoutReservation()
    {
        var (zone, created) = await CreateOwnerAsync(WebServiceCreationMode.Image);
        var deployment = Assert.Single(created.Deployments);
        var relative = string.Join('.', Enumerable.Repeat(new string('a', 63), 3).Append(new string('b', 50)));
        using var invalid = await AddAsync(zone.Id, created.WebServiceId, deployment.DeploymentId, relative);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("domains.hostname.invalid-name", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(0, await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings")
            .CountDocumentsAsync(item => item.TenantDomainZoneId == zone.Id));
    }

    private async Task<(TenantDomainZoneSnapshot Zone, CreateWebServiceResponse Service)> CreateOwnerAsync(
        WebServiceCreationMode mode)
    {
        using var reserve = await Client.PostAsJsonAsync("/api/v1/domain-zones/",
            new
            {
                deploymentTargetId = TestData.DefaultDeploymentTargetId,
                label = "company",
                technicalName = "company"
            });
        Assert.Equal(HttpStatusCode.Created, reserve.StatusCode);
        var zone = (await reserve.Content.ReadFromJsonAsync<TenantDomainZoneSnapshot>())!;
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("hostnames"));
        var body = TestData.CreateWebServiceRequest(group.Id).Body;
        body = body with
        {
            CreationMode = mode,
            Exposure = WebServiceExposureMode.Public,
            Spec = body.Spec with { Image = mode == WebServiceCreationMode.SourceBuild ? null : body.Spec.Image }
        };
        using var create = await Client.PostAsJsonAsync("/api/v1/web-services", body);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        return (zone, (await create.Content.ReadFromJsonAsync<CreateWebServiceResponse>())!);
    }

    private Task<long> RuntimeRequestsAsync() => Fixture.DbContext.OutboxMessages.CountDocumentsAsync(item =>
        item.EventType == "pole.resource-deployments/v1/resource-deployment-reconciliation-requested");

    private Task<HttpResponseMessage> AddAsync(
        string zoneId, string resourceId, string deploymentId, string relative) =>
        Client.PostAsJsonAsync($"/api/v1/web-services/{resourceId}/deployments/{deploymentId}/hostnames",
            new AddWebServiceDeploymentHostnameBody(zoneId, relative, "custom-hostname"));
}

public sealed class HostnameEndpointValidationTests
{
    [Theory]
    [InlineData("api-hostname", "@", true)]
    [InlineData("api-hostname", "api.eu", true)]
    [InlineData("Upper", "api", false)]
    [InlineData("api-hostname", "bad_name", false)]
    [InlineData("api-hostname", "bad.", false)]
    [InlineData("api-hostname", "*.example", false)]
    public void ValidatesTechnicalIdentityAndRelativeDns(string name, string relative, bool valid)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var request = new AddWebServiceDeploymentHostnameRequest(id, id, new(id, relative, name), TestData.TenantId);
        Assert.Equal(valid, new AddWebServiceDeploymentHostnameValidator().Validate(request).IsValid);
    }
}
