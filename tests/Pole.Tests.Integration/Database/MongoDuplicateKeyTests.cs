using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Pole.Database;
using Xunit;

namespace Pole.Tests.Integration.Database;

// Pure driver-error classification: no fixture, connection or server is created.
public sealed class MongoDuplicateKeyTests
{
    private const string ExpectedIndex = "UX_Resources_Tenant_Name";

    [Theory]
    [InlineData(
        "E11000 duplicate key error collection: db.Resources index: UX_Resources_Tenant_Name dup key: {}",
        true)]
    [InlineData(
        "E11000 duplicate key error collection: db.Resources index: UX_Resources_Tenant_Name_extra dup key: {}",
        false)]
    [InlineData("E11000 duplicate key error collection: db.Resources index: _id_ dup key: {}", false)]
    [InlineData(
        "E11000 duplicate key error collection: db.Resources index: other dup key: { value: 'UX_Resources_Tenant_Name' }",
        false)]
    [InlineData(
        "E11000 duplicate key error collection: db.Resources index: UX_Resources_Tenant_Name collation: { locale: 'en', strength: 2 } dup key: {}",
        true)]
    [InlineData(
        "E11000 duplicate key error collection: db.Resources index: UX_Resources_Tenant_Name_extra collation: { locale: 'en' } dup key: {}",
        false)]
    [InlineData("unknown duplicate format { value: 'index: UX_Resources_Tenant_Name dup key:' }", false)]
    [InlineData("duplicate UX_Resources_Tenant_Name", false)]
    public void Command_error_matches_only_the_exact_index_marker(string message, bool expected)
    {
        var error = Error(message);
        Assert.Equal(expected, MongoDuplicateKey.IsForIndex(error, ExpectedIndex));
    }

    [Fact]
    public void Structured_index_identity_takes_precedence_over_message()
    {
        var error = Error($"index: {ExpectedIndex} dup key: {{}}", indexName: "other");
        Assert.False(MongoDuplicateKey.IsForIndex(error, ExpectedIndex));
        Assert.True(MongoDuplicateKey.IsForIndex(Error("no marker", indexName: ExpectedIndex), ExpectedIndex));
    }

    [Fact]
    public void Non_duplicate_errors_are_never_mapped_to_name_conflicts()
    {
        Assert.False(
            MongoDuplicateKey.IsForIndex(Error($"index: {ExpectedIndex} dup key: {{}}", code: 13), ExpectedIndex));
        Assert.False(MongoDuplicateKey.IsForIndex(new InvalidOperationException(ExpectedIndex), ExpectedIndex));
    }

    [Fact]
    public void Identity_index_retry_also_requires_the_expected_collection()
    {
        var error = Error("E11000 duplicate key error collection: db.Resources index: _id_ dup key: {}");
        Assert.True(MongoDuplicateKey.IsForIdIndex(error, "Resources"));
        Assert.False(MongoDuplicateKey.IsForIdIndex(error, "OtherResources"));
        Assert.False(MongoDuplicateKey.IsForIdIndex(Error("index: _id_ dup key: {}"), "Resources"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Identity_conflicts_require_an_explicit_collection(string collection)
    {
        Assert.Throws<ArgumentException>(() => MongoDuplicateKey.IsForIdIndex(new Exception(), collection));
    }

    [Fact]
    public void Write_concern_uncertainty_is_not_reported_as_a_name_conflict()
    {
        var error = Error(
            "E11000 duplicate key error collection: db.Resources index: UX_Resources_Tenant_Name dup key: {}",
            writeConcernError: true);
        Assert.False(MongoDuplicateKey.IsForIndex(error, ExpectedIndex));
    }

    private static MongoCommandException Error(
        string message,
        int code = 11000,
        string? indexName = null,
        bool writeConcernError = false)
    {
        var reply = new BsonDocument { ["code"] = code, ["errmsg"] = message };
        if (indexName is not null)
            reply["indexName"] = indexName;
        if (writeConcernError)
            reply["writeConcernError"] = new BsonDocument("code", 64);
        return new MongoCommandException(
            new ConnectionId(new ServerId(new ClusterId(), new IPEndPoint(IPAddress.Loopback, 27017))),
            message,
            new BsonDocument("insert", "Resources"),
            reply);
    }
}
