using MongoDB.Bson.Serialization.Attributes;
using Pole.Database;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class MongoFieldPathTests
{
    [Fact]
    public void Identity_paths_use_the_BSON_id_mapping() => Assert.Equal(
        "_id",
        MongoFields.Field<MappedDocument, string>(document => document.Key));

    [Fact]
    public void Nested_paths_use_each_members_BSON_mapping()
    {
        Assert.Equal(
            "identity.tenant",
            MongoFields.Field<MappedDocument, string>(document => document.Identity.Tenant));
        Assert.Equal(
            "$identity.tenant",
            MongoFields.Reference<MappedDocument, string>(document => document.Identity.Tenant));
    }

    [Fact]
    public void Derived_member_paths_keep_the_root_and_subtype_mappings() => Assert.Equal(
        "connection.token",
        MongoFields.Field<MappedDocument, string>(document => ((CredentialConnection)document.Connection).Credential));

    private sealed record MappedDocument(
        [property: BsonId] string Key,
        [property: BsonElement("identity")] MappedIdentity Identity,
        [property: BsonElement("connection")] Connection Connection
    );

    private sealed record MappedIdentity(
        [property: BsonElement("tenant")] string Tenant
    );

    [BsonKnownTypes(typeof(CredentialConnection))]
    private abstract record Connection;

    private sealed record CredentialConnection(
        [property: BsonElement("token")] string Credential
    ) : Connection;
}
