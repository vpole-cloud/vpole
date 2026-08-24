using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence;

public delegate Task<StorageProfile> PersistCreatedStorageProfile(
    StorageProfile profile,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedStorageProfile(
    IMongoCollection<StorageProfile> storageProfiles,
    RunMongoOutcomeTransaction<StorageProfile> runMongoTransaction)
{
    public Task<StorageProfile> InvokeAsync(
        StorageProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return runMongoTransaction(async (transaction, ct) =>
        {
            if (profile.IsDefault)
            {
                await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    storageProfiles,
                    transaction.Session,
                    exceptProfileId: null,
                    profile.UpdatedAtUtc,
                    ct);
            }

            await storageProfiles.InsertOneAsync(
                transaction.Session,
                profile,
                cancellationToken: ct);
            return transaction.Commit(profile);
        }, cancellationToken);
    }
}
