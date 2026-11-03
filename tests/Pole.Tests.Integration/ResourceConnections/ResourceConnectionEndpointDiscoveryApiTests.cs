using System.Collections.Immutable;
using System.Net;
using Pole.Tests.Integration.Http;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

public sealed class ResourceConnectionEndpointDiscoveryApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId SourceType = ResourceTypeId.Create("pole/web-service");
    private static readonly ResourceTypeId DestinationType = RedisConnectionEndpoints.ResourceType;

    [Fact]
    public async Task Viewer_reads_canonical_metadata_without_credentials_or_native_locations_and_GET_does_not_fence()
    {
        var destination = await SeedAsync(DestinationType);
        var source = await SeedAsync(SourceType);
        var sourceWeb = TestData.SeedWebService(source.Group.Id, id: source.Id) with { IsolationScopeId = source.Group.DefaultIsolationScopeId };
        sourceWeb = sourceWeb with { Spec = sourceWeb.Spec with { Config = sourceWeb.Spec.Config with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("PRIVATE_SETTING", "private-environment-canary") } } };
        await Fixture.Database.GetCollection<WebService>("WebServices").InsertOneAsync(sourceWeb);
        var target = Id();
        await PlaceAsync(destination, DestinationType, target);
        await PlaceAsync(source, SourceType, target);
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, tenant with
        {
            Members = ImmutableList.Create(TenantFactory.CreateMember(TestData.TenantId, TestData.UserId, TestData.UserEmail, TenantRole.Viewer))
        });
        var body = await Client.GetStringAsync(UriFor(destination.Id.ToUpperInvariant(), source.Id.ToUpperInvariant()));
        var discovered = JsonSerializer.Deserialize<ResourceConnectionEndpointsDto>(body,
            Pole.ASP.Helpers.PoleHttpJson.CreateSerializerOptions())!;
        Assert.Equal(destination.Id, discovered.ResourceId);
        var endpoint = Assert.Single(discovered.Endpoints);
        Assert.Equal("redis", endpoint.Protocol);
        Assert.Equal(6379, endpoint.Port);
        Assert.Equal("Eligible", discovered.Topology.Status);
        Assert.DoesNotContain("private-native-namespace", body);
        Assert.DoesNotContain("private-environment-canary", body);
        Assert.DoesNotContain("svc.cluster.local", body);
        Assert.Equal(0, (await Fixture.DbContext.ResourceRegistrations.Find(x => x.ResourceId == destination.Id).SingleAsync()).ReferenceFence);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(UriFor(destination.Id))).StatusCode);
    }

    [Fact]
    public async Task Foreign_destination_and_foreign_source_have_the_same_safe_missing_response()
    {
        var destination = await SeedAsync(DestinationType);
        var source = await SeedAsync(SourceType);
        var foreignDestination = await SeedAsync(DestinationType, Id());
        var foreignSource = await SeedAsync(SourceType, Id());
        using var destinationResponse = await Client.GetAsync(UriFor(foreignDestination.Id, source.Id));
        using var sourceResponse = await Client.GetAsync(UriFor(destination.Id, foreignSource.Id));
        Assert.Equal(HttpStatusCode.NotFound, destinationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sourceResponse.StatusCode);
        var first = await destinationResponse.Content.ReadAsStringAsync();
        var second = await sourceResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("redis", first);
        Assert.DoesNotContain("private", second);
        using var firstJson = JsonDocument.Parse(first);
        using var secondJson = JsonDocument.Parse(second);
        Assert.Equal(firstJson.RootElement.GetProperty("code").GetString(), secondJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Distinguishes_unsupported_resource_empty_endpoint_set_and_missing_provider_resource()
    {
        var unsupported = await SeedAsync(ResourceTypeId.Create("example/unsupported"));
        var empty = await SeedAsync(SourceType);
        var emptyWeb = TestData.SeedWebService(empty.Group.Id, id: empty.Id) with { IsolationScopeId = empty.Group.DefaultIsolationScopeId };
        emptyWeb = emptyWeb with { Spec = emptyWeb.Spec with { Config = emptyWeb.Spec.Config with { PortMappings = [] } } };
        await Fixture.Database.GetCollection<WebService>("WebServices").InsertOneAsync(emptyWeb);
        var unsupportedResult = await Client.GetFromJsonAsync<ResourceConnectionEndpointsDto>($"/api/v1/resource-connections/endpoints?resourceType=example%2Funsupported&resourceId={unsupported.Id}");
        Assert.Equal("Unsupported", unsupportedResult!.Availability);
        var emptyResult = await Client.GetFromJsonAsync<ResourceConnectionEndpointsDto>($"/api/v1/resource-connections/endpoints?resourceType=pole%2Fweb-service&resourceId={empty.Id}");
        Assert.Equal("Supported", emptyResult!.Availability);
        Assert.Empty(emptyResult.Endpoints);
        Assert.Equal("NotEvaluated", emptyResult.Topology.Status);
        var missing = await SeedAsync(DestinationType);
        await Fixture.Database.GetCollection<Redis>("RedisServices").DeleteOneAsync(x => x.Id == missing.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(UriFor(missing.Id))).StatusCode);
    }

    [Theory]
    [InlineData(false, false, "SourceHasNoPlacements")]
    [InlineData(true, false, "DestinationTargetMissing")]
    [InlineData(true, true, "DestinationTargetAmbiguous")]
    public async Task Reports_fixed_topology_reasons(bool withSource, bool ambiguous, string reason)
    {
        var source = await SeedAsync(SourceType);
        var destination = await SeedAsync(DestinationType);
        var target = Id();
        if (withSource) await PlaceAsync(source, SourceType, target);
        await PlaceAsync(destination, DestinationType, ambiguous ? target : Id());
        if (ambiguous) await PlaceAsync(destination, DestinationType, target);
        var result = await Client.GetFromJsonAsync<ResourceConnectionEndpointsDto>(UriFor(destination.Id, source.Id));
        Assert.Equal("Ineligible", result!.Topology.Status);
        Assert.Equal(reason, result.Topology.ReasonCode);
    }

    [Fact]
    public async Task Stale_cross_group_and_scope_discovery_cannot_admit_a_new_uncovered_source_target()
    {
        var source = await SeedAsync(SourceType);
        var destination = await SeedAsync(DestinationType);
        var target = Id();
        await PlaceAsync(source, SourceType, target);
        await PlaceAsync(destination, DestinationType, target);
        var discovered = await Client.GetFromJsonAsync<ResourceConnectionEndpointsDto>(UriFor(destination.Id, source.Id));
        Assert.Equal("Eligible", discovered!.Topology.Status);
        Assert.NotEqual(source.Group.DefaultIsolationScopeId, destination.Group.DefaultIsolationScopeId);
        await PlaceAsync(source, SourceType, Id());
        var response = await Client.PostAsJsonAsync("/api/v1/resource-connections", new CreateResourceConnectionBody("stale", SourceType.Value, source.Id, DestinationType.Value, destination.Id, "redis"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections").CountDocumentsAsync(x => x.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync("pole.resource-connections/v1/resource-connection-created"));
        var latest = await Client.GetFromJsonAsync<ResourceConnectionEndpointsDto>(UriFor(destination.Id, source.Id));
        Assert.Equal("DestinationTargetMissing", latest!.Topology.ReasonCode);
    }

    [Theory]
    [InlineData("resourceType=invalid&resourceId=507f1f77bcf86cd799439011")]
    [InlineData("resourceType=pole%2Fredis&resourceId=invalid")]
    [InlineData("resourceType=pole%2Fredis&resourceId=507f1f77bcf86cd799439011&sourceResourceType=pole%2Fweb-service")]
    [InlineData("resourceType=pole%2Fredis&resourceId=507f1f77bcf86cd799439011&sourceResourceId=507f1f77bcf86cd799439011")]
    public async Task Rejects_invalid_identity_or_partial_source_pair(string query) => Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/api/v1/resource-connections/endpoints?{query}")).StatusCode);

    private sealed record Reference(
        string Id,
        ResourceGroup Group);
    private async Task<Reference> SeedAsync(ResourceTypeId type, string tenant = TestData.TenantId)
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup($"group-{Id()[..12]}", tenant));
        var id = Id();
        await Fixture.DbContext.ResourceRegistrations.InsertOneAsync(ResourceRegistrationFactory.Create(tenant, group.Id, type, id, "Example resource",
            type.Value.StartsWith("pole/", StringComparison.Ordinal) ? null : type.Value.Split('/')[0], new ResourceRegistrationFactory.CreateOptions(Id(), DateTime.UtcNow)));
        if (type == DestinationType) await Fixture.Database.GetCollection<Redis>("RedisServices").InsertOneAsync(RedisFactory.Create(tenant, $"cache-{Id()}", group.Id, new RedisSpec(new Storage(1, Id())), RedisFactory.DefaultCreateOptions with { Id = id }));
        return new(id, group);
    }

    private async Task PlaceAsync(Reference reference, ResourceTypeId type, string target)
    {
        var placement = ResolvedResourcePlacement.Create(Id(), target, "private-native-namespace", 1, 1, null, null, ResourceDeploymentTestData.Compute(), DateTime.UtcNow);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(ResourceDeploymentFactory.Create(reference.Group.TenantId, type, reference.Id, reference.Group.Id, reference.Group.DefaultIsolationScopeId,
            placement, ResourcePlacementCapability.MultiTarget, true, ResourceDeploymentFactory.DefaultOptions with
            {
                Id = Id(),
                CreatedAtUtc = DateTime.UtcNow
            }));
    }

    private static string UriFor(string id, string? source = null) => $"/api/v1/resource-connections/endpoints?resourceType=pole%2Fredis&resourceId={id}" + (source is null ? "" : $"&sourceResourceType=pole%2Fweb-service&sourceResourceId={source}");
    private static string Id() => ObjectId.GenerateNewId().ToString();
}
