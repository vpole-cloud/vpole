using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Defaults;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Events;

namespace Pole.Features.StorageProfiles.Application.Catalog;

public union PersistUpdatedStorageProfileOutcome(
    StorageProfile,
    StorageProfileRevisionConflict);

public delegate Task<PersistUpdatedStorageProfileOutcome>
    PersistUpdatedStorageProfile(
        StorageProfile current,
        StorageProfile replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedStorageProfile(
    IMongoCollection<StorageProfile> storageProfiles,
    RunMongoOutcomeTransaction<PersistUpdatedStorageProfileOutcome>
        runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    StorageProfileHistory history)
{
    public Task<PersistUpdatedStorageProfileOutcome> InvokeAsync(
        StorageProfile current,
        StorageProfile replacement,
        CancellationToken cancellationToken = default)
    {
        EnsureSameIdentity(current, replacement);
        return runMongoTransaction(async (transaction, ct) =>
            {
                if (current == replacement)
                {
                    var exists = await storageProfiles.Find(transaction.Session,
                        StorageProfileFilters.AtSnapshot(current)).AnyAsync(ct);
                    return exists
                        ? transaction.Commit(current)
                        : transaction.Abort(new StorageProfileRevisionConflict(current.Id));
                }

                if (replacement.Revision != checked(current.Revision + 1))
                    throw new ArgumentException("A changed profile must advance its revision exactly once.");

                if (replacement.IsDefault)
                {
                    var demoted = await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                        storageProfiles,
                        history,
                        transaction.Session,
                        replacement.Id,
                        replacement.UpdatedAtUtc,
                        ct);
                    if (demoted is StorageProfileRevisionConflict conflict)
                        return transaction.Abort(conflict);
                }

                var replacementWrite = await storageProfiles.ReplaceOneAsync(
                    transaction.Session,
                    StorageProfileFilters.AtSnapshot(current),
                    replacement,
                    cancellationToken: ct);
                if (replacementWrite.ModifiedCount != 1)
                    return transaction.Abort(new StorageProfileRevisionConflict(current.Id));
                await history.StoreAsync(current, transaction.Session, ct);
                await history.StoreAsync(replacement, transaction.Session, ct);
                await storeOutboxWrites(transaction.Session,
                    [OutboxWrite.Create(new StorageProfileCatalogChangedEvent(
                        replacement.Id, replacement.Revision, replacement.TechnicalName,
                        Pole.Features.Events.Abstractions.EventCore.Create()))],
                    ct);
                return transaction.Commit(replacement);
            },
            cancellationToken);
    }

    private static void EnsureSameIdentity(StorageProfile current, StorageProfile replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (current.Id != replacement.Id || current.TechnicalName != replacement.TechnicalName)
        {
            throw new ArgumentException(
                "A storage-profile replacement must preserve the profile identity.",
                nameof(replacement));
        }
    }
}
