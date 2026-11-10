using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Catalog;

public delegate Task<ComputeProfile?> LoadComputeProfileById(
    string computeProfileId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadComputeProfileById(
    IMongoCollection<ComputeProfile> computeProfiles
)
{
    public async Task<ComputeProfile?> InvokeAsync(
        string computeProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await computeProfiles.Find(ComputeProfileFilters.ById(computeProfileId))
            .FirstOrDefaultAsync(cancellationToken);
        return profile;
    }
}
