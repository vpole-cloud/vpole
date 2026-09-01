using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Search.Application.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class MigrationRunnerTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task RunMigrations_AppliesPendingMigration_AndStoresHistory()
    {
        var migration = new TrackingMigration(new ObjectId("67ec10000000000000000001"), "Create search indexes");
        var runner = CreateRunner(migration);

        await runner.RunMigrations();

        Assert.Equal(1, migration.InvocationCount);

        var applied = await Fixture.Database
            .GetCollection<BsonDocument>("__migrations")
            .Find(new BsonDocument("_id", migration.Id))
            .FirstOrDefaultAsync();

        Assert.NotNull(applied);
        Assert.Equal(migration.Name, applied["Name"].AsString);
    }

    [Fact]
    public async Task RunMigrations_SkipsAlreadyAppliedMigration()
    {
        var migration = new TrackingMigration(new ObjectId("67ec10000000000000000002"), "Already applied");

        await Fixture.Database
            .GetCollection<BsonDocument>("__migrations")
            .InsertOneAsync(new BsonDocument
            {
                ["_id"] = migration.Id,
                ["Name"] = migration.Name,
                ["Description"] = migration.Description,
                ["AppliedOnUtc"] = DateTime.UtcNow
            });

        var runner = CreateRunner(migration);

        await runner.RunMigrations();

        Assert.Equal(0, migration.InvocationCount);
    }

    [Fact]
    public async Task RunMigrations_IsSafeUnderConcurrentExecution()
    {
        var migration = new TrackingMigration(
            new ObjectId("67ec10000000000000000003"),
            "Concurrent migration",
            upDelay: TimeSpan.FromMilliseconds(500));

        var firstRunner = CreateRunner(migration);
        var secondRunner = CreateRunner(migration);

        await Task.WhenAll(
            firstRunner.RunMigrations(),
            secondRunner.RunMigrations());

        Assert.Equal(1, migration.InvocationCount);

        var appliedCount = await Fixture.Database
            .GetCollection<BsonDocument>("__migrations")
            .CountDocumentsAsync(new BsonDocument("_id", migration.Id));

        Assert.Equal(1L, appliedCount);

        var lockCount = await Fixture.Database
            .GetCollection<BsonDocument>("__migration_locks")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);

        Assert.Equal(0L, lockCount);
    }

    [Fact]
    public async Task RunMigrations_WithSearchMigration_CreatesWeightedKeyIndexes()
    {
        var runner = CreateRunner(new SearchDocumentIndexesMigration());

        await runner.RunMigrations();

        var indexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchDocuments")
            .Indexes
            .ListAsync();

        var indexDocuments = await indexes.ToListAsync();
        var indexNames = indexDocuments.Select(index => index["name"].AsString).ToList();

        Assert.Contains("IX_SearchDocuments_Source_Key", indexNames);
        Assert.Contains("IX_SearchDocuments_SearchValue_SourceApiName", indexNames);
        Assert.Contains("IX_SearchDocuments_SourceSortOrder_SearchValue_Weight", indexNames);

        var sourceIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchSourceDocuments")
            .Indexes
            .ListAsync();

        var sourceIndexDocuments = await sourceIndexes.ToListAsync();
        var sourceIndexNames = sourceIndexDocuments.Select(index => index["name"].AsString).ToList();

        Assert.Contains("IX_SearchSourceDocuments_Source", sourceIndexNames);
        Assert.Contains("IX_SearchSourceDocuments_SearchKeys_Value", sourceIndexNames);
        Assert.Contains("IX_SearchSourceDocuments_SourceApiName_SearchKeys_Value", sourceIndexNames);

        var versionIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SearchProjectionVersions")
            .Indexes
            .ListAsync();
        var versionIndexNames = (await versionIndexes.ToListAsync())
            .Select(index => index["name"].AsString)
            .ToList();

        Assert.Contains("IX_SearchProjectionVersions_Source", versionIndexNames);
    }

    private MigrationRunner CreateRunner(params IMigration[] migrations)
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        return new MigrationRunner(
            Fixture.Database,
            loggerFactory.CreateLogger<MigrationRunner>(),
            migrations);
    }

    private sealed class TrackingMigration(ObjectId id, string name, TimeSpan? upDelay = null) : IMigration
    {
        private int _invocationCount;

        public ObjectId Id { get; } = id;
        public string Name { get; } = name;
        public string Description { get; } = $"Test migration {name}";
        public int InvocationCount => _invocationCount;

        public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);

            if (upDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await context.GetCollection<BsonDocument>("test-migration-artifacts").InsertOneAsync(
                new BsonDocument
                {
                    ["migrationId"] = Id,
                    ["name"] = Name
                },
                cancellationToken: cancellationToken);
        }
    }
}
