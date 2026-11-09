using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.Outbox.Contracts;
using Pole.Testing;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestDatabaseScope(
    IMongoDatabase database,
    IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full) : IAsyncDisposable
{
    private readonly ServiceProvider _outbox = new ServiceCollection()
        .AddSingleton(database).AddOutboxPersistence().BuildServiceProvider();
    private readonly Lock _preparationLock = new();
    private Task? _preparation;
    private Task? _catalogPreparation;
    private bool _disposed;

    public IMongoDatabase Database { get; } = database;
    public IntegrationSchemaProfile SchemaProfile { get; } = Enum.IsDefined(schemaProfile)
        ? schemaProfile
        : throw new ArgumentOutOfRangeException(nameof(schemaProfile));
    public IMongoClient Client => Database.Client;
    public MongoTestStore DbContext { get; } = new(database.Client, database);
    public StoreOutboxWrites StoreOutboxWrites => _outbox.GetRequiredService<StoreOutboxWrites>();

    public Task PrepareAsync()
    {
        lock (_preparationLock)
            return _preparation ??= IntegrationDatabaseSchema.PrepareAsync(this);
    }

    public Task PrepareNamedCatalogAsync()
    {
        if (SchemaProfile is not (IntegrationSchemaProfile.Full or IntegrationSchemaProfile.Catalogs))
            throw new InvalidOperationException("Named catalog seeding requires a Full or Catalogs schema profile.");
        lock (_preparationLock)
            return _catalogPreparation ??= PrepareNamedCatalogCoreAsync();
    }

    private async Task PrepareNamedCatalogCoreAsync()
    {
        await PrepareAsync();
        await GitOps.NamedCatalogTestData.SeedAsync(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _outbox.DisposeAsync();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var timing = new TestTimings(GetType(), fixture: true).Measure("fixture.database.drop");
        await Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName, cleanup.Token);
    }
}
