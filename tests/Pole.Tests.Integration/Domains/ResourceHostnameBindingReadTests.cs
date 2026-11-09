using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceHostnameBindingReadTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Transactional_resource_read_includes_orphan_placements_and_is_tenant_and_type_scoped()
    {
        var resourceId = ObjectId.GenerateNewId().ToString();
        var type = ResourceTypeId.Create("pole/web-service");
        var first = Binding(Data.TenantId, type, resourceId, "first");
        var orphan = Binding(Data.TenantId, type, resourceId, "orphan");
        var foreign = Binding(ObjectId.GenerateNewId().ToString(), type, resourceId, "foreign");
        var otherType = Binding(Data.TenantId, ResourceTypeId.Create("other/kind"), resourceId, "other");
        var bindings = Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings");
        await bindings.InsertManyAsync([first, orphan, foreign, otherType]);
        var read = Services.GetRequiredService<LoadHostnameBindingsForResourceInTransaction>();
        using var session = await Fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => read(new(Data.TenantId, type, resourceId),
            session, default));
        session.StartTransaction();
        var found = await read(new(Data.TenantId, type, resourceId), session, default);
        await session.AbortTransactionAsync();
        Assert.Equal(new[] { first.Id, orphan.Id }.Order(StringComparer.Ordinal),
            found.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.NotEqual(first.ResourceDeploymentId, orphan.ResourceDeploymentId);
    }

    private static HostnameBinding Binding(string tenant, ResourceTypeId type, string resourceId, string name) =>
        HostnameBindingFactory.CreateCustom(tenant, ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), type, resourceId, ObjectId.GenerateNewId().ToString(), name,
            DnsName.Create("apps.example.test"), name,
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
}
