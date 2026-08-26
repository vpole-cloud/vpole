using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Persistence.Defaults;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Persistence.Mutations;

public delegate Task<Result<StorageProfile, StorageProfileRevisionConflict>>
    PersistUpdatedStorageProfile(
        StorageProfile current,
        StorageProfile replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedStorageProfile(
    IMongoCollection<StorageProfile> storageProfiles,
    RunMongoOutcomeTransaction<Result<StorageProfile, StorageProfileRevisionConflict>>
        runMongoTransaction)
{
    public Task<Result<StorageProfile, StorageProfileRevisionConflict>> InvokeAsync(
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
            return replacementWrite.ModifiedCount == 1
                ? transaction.Commit(new Ok<StorageProfile>(replacement))
                : transaction.AbortError(new StorageProfileRevisionConflict(current.Id));
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
