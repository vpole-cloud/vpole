using Aspire.Hosting.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    // Fault injection belongs only to the disposable test database, never a production endpoint.
    public async Task InjectResourceGroupDriftAsync(string resourceId, CancellationToken ct)
    {
        var app = _application ?? throw new InvalidOperationException("The E2E graph is not initialized.");
        var connection = await app.GetConnectionStringAsync("pole-database", ct);
        var client = new MongoClient(connection);
        var groups = client.GetDatabase("pole-database").GetCollection<BsonDocument>("ResourceGroups");
        var result = await groups.UpdateOneAsync(
            new BsonDocument { { "_id", ObjectId.Parse(resourceId) },
                { "TenantId", ObjectId.Parse(Alpha.DefaultTenantId) } },
            new BsonDocument { { "$set", new BsonDocument("Description", "controlled E2E configuration drift") },
                { "$inc", new BsonDocument("Revision", 1L) } }, cancellationToken: ct);
        Assert.Equal(1, result.ModifiedCount);
    }
}
