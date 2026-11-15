using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Cli.Api;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Endpoints;
using Pole.Features.SeaweedFS.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests.Integration;

[Collection("seaweedfs-mongo")]
public sealed class SeaweedFSConnectionDiscoveryTests(
    MongoReplicaSetFixture fixture
) : SeaweedFSHostTestBase(fixture)
{
    [Fact]
    public async Task Concrete_S3_discovery_and_CLI_name_resolution_reject_foreign_deleting_and_unapplied_resources()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host);
        using var created = await client.PostAsJsonAsync(Route, await SeedCreateAsync(), Json);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<CreateSeaweedFSServiceResponse>(Json))!;
        var id = service.SeaweedFSId;
        var endpointUri = $"/api/v1/resource-connections/endpoints?resourceType=pole%2Fseaweedfs&resourceId={id}";
        using var discovered = await client.GetAsync(endpointUri);
        Assert.True(discovered.StatusCode == HttpStatusCode.OK, await discovered.Content.ReadAsStringAsync());
        var endpoints = (await discovered.Content.ReadFromJsonAsync<ResourceConnectionEndpointsDto>(Json))!;
        var endpoint = Assert.Single(endpoints.Endpoints);
        Assert.Equal("s3", endpoint.Id);
        Assert.Equal("http", endpoint.Protocol);
        Assert.Equal(8333, endpoint.Port);

        var grant = PrivateAccessGrantFactory.Create(
            TestPlatformDefaults.TenantId,
            TestPlatformDefaults.UserId,
            "pole/seaweedfs",
            id,
            service.Placement.DeploymentId,
            "s3",
            TestPlatformDefaults.UserId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1));
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants").InsertOneAsync(grant);
        var cli = new PrivateAccessApiClient(client, client.BaseAddress!, _ => Task.FromResult("test-token"));
        var resources = Fixture.Database.GetCollection<SeaweedFSService>("SeaweedFS");
        var resource = await resources.Find(item => item.Id == id).SingleAsync();
        var foreignTenant = ObjectId.GenerateNewId().ToString();
        await host.SeedTenantAsync(foreignTenant);
        await resources.InsertOneAsync(
            resource with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                TenantId = foreignTenant,
                ResourceGroupId = ObjectId.GenerateNewId().ToString(),
                AdminSecretId = ObjectId.GenerateNewId().ToString()
            });
        var unresolved = await cli.GetResourceGrantsAsync(
            TestPlatformDefaults.TenantId,
            "seaweedfs",
            resource.Name,
            deploymentId: null,
            endpointId: "s3",
            CancellationToken.None);
        Assert.Empty(unresolved);
        var deployments = Fixture.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        var deployment = await deployments.Find(item => item.Id == service.Placement.DeploymentId).SingleAsync();
        await deployments.ReplaceOneAsync(
            item => item.Id == deployment.Id,
            deployment with
            {
                State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow),
                ObservedGeneration = deployment.DesiredGeneration
            });
        var available = await cli.GetResourceGrantsAsync(
            TestPlatformDefaults.TenantId,
            "seaweedfs",
            resource.Name,
            deploymentId: null,
            endpointId: "s3",
            CancellationToken.None);
        Assert.Equal(grant.Id, Assert.Single(available).GrantId);
        Assert.Equal("s3", available[0].EndpointId);
        var missing = await cli.GetResourceGrantsAsync(
            TestPlatformDefaults.TenantId,
            "seaweedfs",
            "missing",
            deploymentId: null,
            endpointId: "s3",
            CancellationToken.None);
        Assert.Empty(missing);

        var foreign = await cli.GetResourceGrantsAsync(
            foreignTenant,
            "seaweedfs",
            resource.Name,
            deploymentId: null,
            endpointId: "s3",
            CancellationToken.None);
        Assert.Empty(foreign);
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, endpointUri);
        foreignRequest.Headers.Add("X-Pole-Tenant-ID", foreignTenant);
        using var foreignEndpoint = await client.SendAsync(foreignRequest);
        Assert.Equal(HttpStatusCode.NotFound, foreignEndpoint.StatusCode);

        using var deleted = await client.DeleteAsync(Route + id + "?expectedRevision=1");
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        var deleting = await cli.GetResourceGrantsAsync(
            TestPlatformDefaults.TenantId,
            "seaweedfs",
            resource.Name,
            deploymentId: null,
            endpointId: "s3",
            CancellationToken.None);
        Assert.Empty(deleting);
        using var deletedEndpoint = await client.GetAsync(endpointUri);
        Assert.Equal(HttpStatusCode.NotFound, deletedEndpoint.StatusCode);
    }
}
