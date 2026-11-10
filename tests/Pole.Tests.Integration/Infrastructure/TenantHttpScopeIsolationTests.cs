using System.Net;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Testing;
using Pole.Tests.Integration.Http;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class TenantHttpScopeIsolationTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task IndependentHttpIdentitiesCannotReadUpdateOrDeleteTheOtherTenant()
    {
        var other = IntegrationTestScope.Create(Fixture);
        await other.SeedAsync();
        var groups = new ResourceGroupTestData(Fixture.Database);
        var own = await groups.CreateAsync(Data.TenantId, "same-name");
        var foreign = await groups.CreateAsync(other.TenantId, "same-name");
        await using var otherApi = await TestApiApplication.StartAsync(Fixture, other);

        using var ownRead = await Client.GetAsync("/api/v1/resource-groups/" + own.Id);
        using var otherRead = await otherApi.Client.GetAsync("/api/v1/resource-groups/" + foreign.Id);
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherRead.StatusCode);
        using var hiddenRead = await Client.GetAsync("/api/v1/resource-groups/" + foreign.Id);
        using var hiddenDelete = await Client.DeleteAsync("/api/v1/resource-groups/" + foreign.Id);
        using var hiddenUpdate = await Client.PutAsJsonAsync(
            "/api/v1/resource-groups/" + foreign.Id,
            new { name = "changed", description = "", tags = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NotFound, hiddenRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenUpdate.StatusCode);
        using var selection = new HttpRequestMessage(HttpMethod.Get, "/api/v1/resource-groups/" + foreign.Id);
        selection.Headers.Add(TenantHeaders.TenantId, other.TenantId);
        using var forbidden = await Client.SendAsync(selection);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            1,
            await Fixture.DbContext.ResourceGroups.CountDocumentsAsync(group =>
                group.Id == foreign.Id && group.TenantId == other.TenantId));
    }
}
