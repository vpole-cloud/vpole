using Pole.Migrations;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Search.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class MigrationRunnerTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task DifferentFeaturesMayUseTheSameMigrationId_AndEachRunsOnce()
    {
        var id = new ObjectId("67ec10000000000000000024");
        var first = new TrackingMigration(id, "first");
        var second = new TrackingMigration(id, "second");
        var runner = CreateRunner(new FeatureScopedMigration("acme.first", first), new FeatureScopedMigration("acme.second", second));
        await runner.RunMigrations();
        await runner.RunMigrations();
        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
        var ids = await Fixture.Database.GetCollection<BsonDocument>("__migrations")
            .Find(new BsonDocument()).Project(doc => doc["_id"]).ToListAsync();
        Assert.Contains((BsonValue)$"acme.first/{id}", ids);
        Assert.Contains((BsonValue)$"acme.second/{id}", ids);
    }

    [Fact]
    public async Task FeatureMigrations_RunAfterPlatform_AndPreserveRegistrationOrder()
    {
        var applied = new List<string>();
        var platform = new TrackingMigration(new ObjectId("67ec10000000000000000023"), "platform", onApply: () => applied.Add("platform"));
        var dependency = new TrackingMigration(new ObjectId("67ec10000000000000000022"), "dependency", onApply: () => applied.Add("dependency"));
        var dependent = new TrackingMigration(new ObjectId("67ec10000000000000000021"), "dependent", onApply: () => applied.Add("dependent"));
        await CreateRunner(new FeatureScopedMigration("test.dependency", dependency),
            new FeatureScopedMigration("test.dependent", dependent), platform).RunMigrations();
        Assert.Equal(new[] { "platform", "dependency", "dependent" }, applied);
    }

    [Fact]
    public async Task FeatureMigrations_RejectDuplicateScopedIdsBeforeApplying()
    {
        var migration = new TrackingMigration(new ObjectId("67ec10000000000000000013"), "Duplicate");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(
            new FeatureScopedMigration("test.duplicate", migration),
            new FeatureScopedMigration("test.duplicate", migration)).RunMigrations());
        Assert.Equal(0, migration.InvocationCount);
    }

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

    private sealed class TrackingMigration(ObjectId id, string name, TimeSpan? upDelay = null, Action? onApply = null) : IMigration
    {
        private int _invocationCount;

        public ObjectId Id { get; } = id;
        public string Name { get; } = name;
        public string Description { get; } = $"Test migration {name}";
        public int InvocationCount => _invocationCount;

        public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            onApply?.Invoke();

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
