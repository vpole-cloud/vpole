using MongoDB.Driver;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class MongoIntegrationTestBase(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    protected IMongoDatabase Database { get; private set; } = null!;
    protected MongoTestStore DbContext { get; private set; } = null!;

    public virtual Task InitializeAsync()
    {
        Database = fixture.Client.GetDatabase("pole-mongo-test-" + Guid.NewGuid().ToString("N"));
        DbContext = new MongoTestStore(fixture.Client, Database);
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Database.Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName, cleanup.Token);
    }
}
