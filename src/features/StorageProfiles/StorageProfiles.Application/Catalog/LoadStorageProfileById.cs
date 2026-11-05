using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Catalog;

public delegate Task<StorageProfile?> LoadStorageProfileById(
    string storageProfileId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadStorageProfileById(
    IMongoCollection<StorageProfile> storageProfiles)
{
    public async Task<StorageProfile?> InvokeAsync(
        string storageProfileId,
        CancellationToken cancellationToken = default)
    {
        return await storageProfiles
            .Find(StorageProfileFilters.ById(storageProfileId))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
