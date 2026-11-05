using Pole.Architecture;
using Pole.Migrations;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Pole.Database;

/// <summary>
/// The MigrationRunner class is responsible for running database migrations
/// in a MongoDB database. It takes a list of migrations and applies them to
/// the database in order, ensuring that each migration is applied only once.
/// The runner will keep track of which migrations have been applied in a
/// special collection in the database, and will skip any migrations that have
/// already been applied. This allows you to safely run the migrations multiple
/// times without worrying about applying the same migration twice.
/// </summary>
/// <param name="database"></param>
/// <param name="migrations"></param>
public class MigrationRunner(
    IMongoDatabase database,
    ILogger<MigrationRunner> logger,
    IEnumerable<IMigration> migrations)
{
    private const string AppliedMigrationsCollectionName = "__migrations";
    private const string MigrationLocksCollectionName = "__migration_locks";
    private const string GlobalLockId = "global";
    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockAcquireTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(2);

    [ViolateArchitecture(ArchitectureRules.RawBson,
        "Migration bookkeeping stores both ObjectId migration identities and feature-scoped string " +
        "identities in the same _id field.")]
    private sealed record AppliedMigration(
        [property: BsonId]
        BsonValue Id,
        string Name,
        string Description,
        DateTime AppliedOnUtc
    );

    private sealed record MigrationLock(
        [property: BsonId]
        string Id,
        string OwnerId,
        DateTime ExpiresAtUtc
    );

    /// <summary>
    /// Runs all pending migrations in order, ensuring that each migration is
    /// applied only once.
    /// </summary>
    /// <param name="cancellationToken"></param>
    [ViolateArchitecture(ArchitectureRules.RawBson,
        "Compare and store migration identities in their exact BSON representation; this shared " +
        "runner is not a migration-project exemption.")]
    public async Task RunMigrations(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Running migrations");

        var appliedMigrations = database.GetCollection<AppliedMigration>(AppliedMigrationsCollectionName);
        var lockOwnerId = Guid.NewGuid().ToString("N");

        await using var migrationLock = await AcquireLockAsync(lockOwnerId, cancellationToken);

        var appliedMigrationIds = await appliedMigrations
            .Find(FilterDefinition<AppliedMigration>.Empty)
            .Project(migration => migration.Id)
            .ToListAsync(cancellationToken);

        var migrationArray = migrations.ToArray();
        var duplicate = migrationArray.GroupBy(DurableId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate migration identity: {duplicate.Key}.");
        var featureOrder = migrationArray.OfType<FeatureScopedMigration>()
            .Select(migration => migration.FeatureId).Distinct()
            .Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var pendingMigrations = migrationArray
            .OrderBy(migration => migration is FeatureScopedMigration feature ? featureOrder[feature.FeatureId] + 1 : 0)
            .ThenBy(migration => migration.Id)
            .Where(migration => !appliedMigrationIds.Contains(DurableId(migration)))
            .ToList();

        if (pendingMigrations.Count == 0)
        {
            logger.LogInformation("No pending migrations found");
            logger.LogInformation("Finished running migrations");
            return;
        }

        foreach (var migration in pendingMigrations)
        {
            await migrationLock.RefreshAsync(cancellationToken);

            logger.LogInformation(
                "Applying migration {MigrationId} - {MigrationName}",
                migration.Id,
                migration.Name);

            await migration.Up(database, cancellationToken);

            await appliedMigrations.InsertOneAsync(
                new AppliedMigration(
                    Id: DurableId(migration),
                    Name: migration.Name,
                    Description: migration.Description,
                    AppliedOnUtc: DateTime.UtcNow),
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Applied migration {MigrationId} - {MigrationName}",
                migration.Id,
                migration.Name);
        }

        logger.LogInformation("Finished running migrations");
    }

    [ViolateArchitecture(ArchitectureRules.RawBson,
        "The migration ledger must retain ObjectId and feature-scoped string identifier representations.")]
    private static BsonValue DurableId(IMigration migration) =>
        migration is FeatureScopedMigration scoped ? scoped.DurableId : (BsonValue)migration.Id;

    private async Task<MigrationLockHandle> AcquireLockAsync(string ownerId, CancellationToken cancellationToken)
    {
        var locks = database.GetCollection<MigrationLock>(MigrationLocksCollectionName);
        var startedAtUtc = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nowUtc = DateTime.UtcNow;
            var expiresAtUtc = nowUtc.Add(LockLeaseDuration);

            var filter = Builders<MigrationLock>.Filter.And(
                Builders<MigrationLock>.Filter.Eq(lockDoc => lockDoc.Id, GlobalLockId),
                Builders<MigrationLock>.Filter.Or(
                    Builders<MigrationLock>.Filter.Lte(lockDoc => lockDoc.ExpiresAtUtc, nowUtc),
                    Builders<MigrationLock>.Filter.Eq(lockDoc => lockDoc.OwnerId, ownerId)
                ));

            var update = Builders<MigrationLock>.Update
                .SetOnInsert(lockDoc => lockDoc.Id, GlobalLockId)
                .Set(lockDoc => lockDoc.OwnerId, ownerId)
                .Set(lockDoc => lockDoc.ExpiresAtUtc, expiresAtUtc);

            MigrationLock? lockDocument;
            try
            {
                lockDocument = await locks.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<MigrationLock>
                    {
                        IsUpsert = true,
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken);
            }
            catch (MongoCommandException ex) when (ex.Code == 11000)
            {
                lockDocument = null;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
            {
                lockDocument = null;
            }

            if (lockDocument is not null && lockDocument.OwnerId == ownerId)
            {
                logger.LogInformation("Acquired migration lock with owner {OwnerId}", ownerId);
                return new MigrationLockHandle(locks, ownerId, logger);
            }

            if (DateTime.UtcNow - startedAtUtc >= LockAcquireTimeout)
            {
                throw new TimeoutException("Timed out while waiting for the migration lock.");
            }

            logger.LogInformation("Waiting for migration lock held by another instance");
            await Task.Delay(LockRetryDelay, cancellationToken);
        }
    }

    private sealed class MigrationLockHandle(
        IMongoCollection<MigrationLock> locks,
        string ownerId,
        ILogger logger) : IAsyncDisposable
    {
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            await locks.UpdateOneAsync(
                lockDoc => lockDoc.Id == GlobalLockId && lockDoc.OwnerId == ownerId,
                Builders<MigrationLock>.Update.Set(lockDoc => lockDoc.ExpiresAtUtc, DateTime.UtcNow.Add(LockLeaseDuration)),
                cancellationToken: cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await locks.DeleteOneAsync(lockDoc => lockDoc.Id == GlobalLockId && lockDoc.OwnerId == ownerId);
                logger.LogInformation("Released migration lock for owner {OwnerId}", ownerId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release migration lock for owner {OwnerId}", ownerId);
            }
        }
    }
}
