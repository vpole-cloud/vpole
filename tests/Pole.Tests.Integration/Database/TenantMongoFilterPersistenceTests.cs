using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

[Collection(IntegrationTestCollection.Name)]
public sealed class TenantMongoFilterPersistenceTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Renamed_object_id_tenant_filters_isolate_reads_updates_and_deletes()
    {
        var tenant = ObjectId.GenerateNewId().ToString();
        var otherTenant = ObjectId.GenerateNewId().ToString();
        var documents = fixture.Database.GetCollection<MappedDocument>("TenantFilterTests");
        await documents.InsertManyAsync([new("first", tenant, "same-name"),
            new("other", otherTenant, "same-name")]);
        var filter = Builders<MappedDocument>.Filter.ForTenant(tenant) &
            Builders<MappedDocument>.Filter.Eq(document => document.Name, "same-name");

        Assert.Equal("first", (await documents.Find(filter).SingleAsync()).Id);
        var updated = await documents.UpdateOneAsync(filter,
            Builders<MappedDocument>.Update.Set(document => document.Name, "updated"));
        Assert.Equal(1, updated.ModifiedCount);
        Assert.Equal("same-name", (await documents.Find(
            Builders<MappedDocument>.Filter.ForTenant(otherTenant)).SingleAsync()).Name);
        var removed = await documents.DeleteManyAsync(Builders<MappedDocument>.Filter.ForTenant(tenant));
        Assert.Equal(1, removed.DeletedCount);
        Assert.Equal("other", (await documents.Find(Builders<MappedDocument>.Filter.Empty).SingleAsync()).Id);
    }

    [Fact]
    public async Task Quota_tenant_identity_is_scoped_through_the_persisted_id_mapping()
    {
        var policies = fixture.Database.GetCollection<TenantQuotaPolicy>("TenantQuotaFilterTests");
        var first = QuotaDomainFactory.CreatePolicy("tenant-a", QuotaDelta.Empty, DateTime.UtcNow);
        var other = QuotaDomainFactory.CreatePolicy("tenant-b", QuotaDelta.Empty, DateTime.UtcNow);
        await policies.InsertManyAsync([first, other]);

        Assert.Equal(first.TenantId, (await policies.Find(
            Builders<TenantQuotaPolicy>.Filter.ForTenant(first.TenantId)).SingleAsync()).TenantId);
        await policies.DeleteOneAsync(Builders<TenantQuotaPolicy>.Filter.ForTenant(first.TenantId));
        Assert.Equal(other.TenantId, (await policies.Find(
            Builders<TenantQuotaPolicy>.Filter.Empty).SingleAsync()).TenantId);
    }

    private sealed record MappedDocument(
        [property: BsonId] string Id,
        [property: BsonElement("owner"), BsonRepresentation(BsonType.ObjectId)] string TenantId,
        string Name) : ITenantOwned;
}
