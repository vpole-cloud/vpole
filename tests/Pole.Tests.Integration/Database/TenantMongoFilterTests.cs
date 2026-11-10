using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Postgres.Domain;
using Pole.Features.Quotas.Domain;
using Pole.Features.Search.Domain;
using Pole.Multitenancy.Primitives;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class TenantMongoFilterTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";

    [Fact]
    public void Tenant_filter_uses_the_concrete_member_mapping()
    {
        Assert.Equal(new BsonDocument("TenantId", TenantId), Render<OrdinaryDocument>());
        Assert.Equal(new BsonDocument("owner", TenantId), Render<AliasedDocument>());
        Assert.Equal(new BsonDocument("owner", new ObjectId(TenantId)), Render<ObjectIdDocument>());
    }

    [Fact]
    public void Tenant_identity_documents_filter_by_their_actual_id_field()
    {
        Assert.Equal(new BsonDocument("_id", TenantId), Render<TenantQuotaPolicy>());
        Assert.Equal(new BsonDocument("_id", TenantId), Render<TenantQuotaUsage>());
    }

    [Fact]
    public void Fsharp_explicit_implementations_keep_the_concrete_property_representation()
    {
        Assert.Equal(new BsonDocument("TenantId", new ObjectId(TenantId)), Render<PostgresCluster>());
        Assert.Equal(new BsonDocument("TenantId", TenantId), Render<SearchDocument>());
        Assert.Equal(new BsonDocument("TenantId", TenantId), Render<SearchSourceDocument>());
        Assert.Equal(new BsonDocument("TenantId", TenantId), Render<SearchProjectionVersionDocument>());
    }

    [Fact]
    public void Resolved_context_filters_use_the_same_concrete_mapping()
    {
        var context = new TenantContext(TenantId, "tenant", TenantRole.Owner, false);
        var filter = Builders<ObjectIdDocument>.Filter.ForTenant(context);
        Assert.Equal(Render<ObjectIdDocument>(), Render(filter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_tenant_ids_are_rejected(string? tenantId) =>
        Assert.ThrowsAny<ArgumentException>(() => Builders<OrdinaryDocument>.Filter.ForTenant(tenantId!));

    [Fact]
    public void Null_context_and_builder_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Builders<OrdinaryDocument>.Filter.ForTenant((TenantContext)null!));
        Assert.Throws<ArgumentNullException>(() => MongoFilterExtensions.ForTenant<OrdinaryDocument>(null!, TenantId));
        Assert.Throws<ArgumentNullException>(() => MongoFilterExtensions.ForTenant<OrdinaryDocument>(
            null!,
            new TenantContext(TenantId, "tenant", TenantRole.Owner, false)));
    }

    private static BsonDocument Render<TDocument>()
        where TDocument : ITenantOwned => Render(Builders<TDocument>.Filter.ForTenant(TenantId));

    private static BsonDocument Render<TDocument>(FilterDefinition<TDocument> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return filter.Render(new RenderArgs<TDocument>(registry.GetSerializer<TDocument>(), registry));
    }

    private sealed record OrdinaryDocument(
        string TenantId
    ) : ITenantOwned;

    private sealed record AliasedDocument(
        [property: BsonElement("owner")] string TenantId
    ) : ITenantOwned;

    private sealed record ObjectIdDocument(
        [property: BsonElement("owner")]
        [property: BsonRepresentation(BsonType.ObjectId)]
        string TenantId
    ) : ITenantOwned;
}
