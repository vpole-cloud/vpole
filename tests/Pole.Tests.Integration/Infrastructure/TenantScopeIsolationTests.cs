using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class TenantScopeIsolationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task ParallelTenantsCanCreateTheSameResourceNameWithoutSharingOutbox()
    {
        var other = IntegrationTestScope.Create(Fixture);
        await other.SeedAsync();
        var otherData = new IntegrationTestData(other);
        await using var provider = TestServiceProviderFactory.Create(Fixture, testScope: other);
        using var scope = provider.CreateScope();
        var results = await Task.WhenAll(
            Mediator.Send(Data.CreateResourceGroupRequest("same-name")),
            scope.ServiceProvider
                .GetRequiredService<IMediator>()
                .Send(otherData.CreateResourceGroupRequest("same-name")));
        var first = results[0].AssertSuccess();
        var second = results[1].AssertSuccess();
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(Data.TenantId, other.TenantId);
        Assert.NotEqual(Data.UserId, other.UserId);
        Assert.Equal(
            1,
            await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.resource-groups/v1/resource-group-created"));
        Assert.Equal(
            1,
            await DbContext.CountOutboxMessagesAsync(other.TenantId, "pole.resource-groups/v1/resource-group-created"));
        Assert.Equal(1, await DbContext.ResourceGroups.CountDocumentsAsync(group => group.TenantId == Data.TenantId));
        Assert.Equal(1, await DbContext.ResourceGroups.CountDocumentsAsync(group => group.TenantId == other.TenantId));
    }

    [Fact]
    public async Task DroppingAnIsolatedDatabaseLeavesTheTenantDatabaseIntact()
    {
        var created = (await Mediator.Send(Data.CreateResourceGroupRequest())).AssertSuccess();
        var isolated
            = new TestDatabaseScope(Fixture.Client.GetDatabase("pole-isolation-canary-" + TestScope.UniqueSuffix));
        await isolated.Database.GetCollection<BsonDocument>("canary").InsertOneAsync(new BsonDocument("owned", true));
        await isolated.DisposeAsync();
        Assert.Equal(
            1,
            await DbContext.ResourceGroups.CountDocumentsAsync(group =>
                group.Id == created.Id && group.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task ConcurrentNarrowPreparationAndApiStartupKeepOnlyTheSelectedSchema()
    {
        await using var narrow = new TestDatabaseScope(
            Fixture.Client.GetDatabase("pole-profile-cache-" + TestScope.UniqueSuffix),
            IntegrationSchemaProfile.Identity);
        await Task.WhenAll(narrow.PrepareAsync(), narrow.PrepareAsync());
        var context = IntegrationTestScope.Create(narrow);
        await context.SeedAsync();
        await using var api = await TestApiApplication.StartAsync(narrow, context);
        using var response = await api.Client.GetAsync($"/api/v1/tenants/{context.TenantId}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IntegrationSchemaProfile.Identity, narrow.SchemaProfile);
        using var collections = await narrow.Database.ListCollectionNamesAsync();
        var names = await collections.ToListAsync();
        Assert.Contains("OutboxMessages", names);
        Assert.Contains("__migrations", names);
        Assert.DoesNotContain("ComputeProfiles", names);
        Assert.DoesNotContain("StorageProfiles", names);
        Assert.DoesNotContain("ResourceDeployments", names);
        await Assert.ThrowsAsync<InvalidOperationException>(() => narrow.PrepareNamedCatalogAsync());
    }
}
