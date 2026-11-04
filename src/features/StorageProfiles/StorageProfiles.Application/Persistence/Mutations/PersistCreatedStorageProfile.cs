using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Events;
using Pole.Features.StorageProfiles.Application.Persistence.Defaults;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Application.Common;

namespace Pole.Features.StorageProfiles.Application.Persistence.Mutations;

public union PersistCreatedStorageProfileOutcome(
    StorageProfile,
    StorageProfileTechnicalNameConflict,
    StorageProfileRevisionConflict);

public delegate Task<PersistCreatedStorageProfileOutcome> PersistCreatedStorageProfile(
    StorageProfile profile,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedStorageProfile(
    IMongoCollection<StorageProfile> storageProfiles,
    RunMongoOutcomeTransaction<PersistCreatedStorageProfileOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    StorageProfileHistory history)
{
    public Task<PersistCreatedStorageProfileOutcome> InvokeAsync(
        StorageProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return runMongoTransaction(async (transaction, ct) =>
            {
                if (profile.IsDefault)
                {
                    var demoted = await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                        storageProfiles,
                        history,
                        transaction.Session,
                        exceptProfileId: null,
                        profile.UpdatedAtUtc,
                        ct);
                    if (demoted is StorageProfileRevisionConflict conflict)
                        return transaction.Abort(conflict);
                }

                try
                {
                    await storageProfiles.InsertOneAsync(transaction.Session, profile, cancellationToken: ct);
                }
                catch (MongoWriteException duplicate) when (MongoDuplicateKey.IsForIndex(
                    duplicate, StorageProfileTechnicalNames.UniqueIndexName))
                {
                    return transaction.Abort(new StorageProfileTechnicalNameConflict(profile.TechnicalName));
                }

                await history.StoreAsync(profile, transaction.Session, ct);
                await storeOutboxWrites(transaction.Session,
                    [OutboxWrite.Create(new StorageProfileCatalogChangedEvent(
                        profile.Id, profile.Revision, profile.TechnicalName,
                        Pole.Features.Events.Abstractions.EventCore.Create()))],
                    ct);
                return transaction.Commit(profile);
            },
            cancellationToken);
    }
}
