using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public async Task AdvanceConsoleSecretReferenceFenceAsync(string secretId, CancellationToken ct)
    {
        // Only the disposable tenant-owned owner document changes. Source/inventory stamps stay untouched,
        // so the browser must send confirmation and observe the server's dependency-version rejection.
        var database = await GitOpsDatabaseAsync(ct);
        var result = await database.GetCollection<BsonDocument>("Secrets").UpdateOneAsync(
            new BsonDocument { { "_id", secretId }, { "TenantId", Alpha.DefaultTenantId } },
            new BsonDocument("$inc", new BsonDocument("ReferenceFence", 1L)), cancellationToken: ct);
        Assert.Equal(1, result.ModifiedCount);
    }
}
