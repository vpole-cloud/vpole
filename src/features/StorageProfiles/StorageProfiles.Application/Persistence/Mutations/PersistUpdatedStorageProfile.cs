using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Events;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Persistence.Defaults;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence.Mutations;

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
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<PersistUpdatedStorageProfileOutcome> InvokeAsync(
        StorageProfile current,
        StorageProfile replacement,
        CancellationToken cancellationToken = default)
    {
        EnsureSameIdentity(current, replacement);
        return runMongoTransaction(async (transaction, ct) =>
        {
            if (replacement.IsDefault)
            {
                await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    storageProfiles,
                    transaction.Session,
                    replacement.Id,
                    replacement.UpdatedAtUtc,
                    ct);
            }

            var replacementWrite = await storageProfiles.ReplaceOneAsync(
                transaction.Session,
                StorageProfileFilters.AtSnapshot(current),
                replacement,
                cancellationToken: ct);
            if (replacementWrite.ModifiedCount != 1)
                return transaction.Abort(new StorageProfileRevisionConflict(current.Id));
            await storeOutboxWrites(transaction.Session,
                [OutboxWrite.Create(new StorageProfileCatalogChangedEvent(replacement.Id, replacement.Revision))], ct);
            return transaction.Commit(replacement);
        }, cancellationToken);
    }

    private static void EnsureSameIdentity(StorageProfile current, StorageProfile replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A storage-profile replacement must preserve the profile identity.",
                nameof(replacement));
        }
    }
}
