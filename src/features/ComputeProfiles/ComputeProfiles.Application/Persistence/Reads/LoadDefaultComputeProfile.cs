using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Reads;

public delegate Task<ComputeProfile?> LoadDefaultComputeProfile(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDefaultComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<ComputeProfile?> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        ComputeProfile? profile = await computeProfiles
            .Find(ComputeProfileFilters.Default())
            .FirstOrDefaultAsync(cancellationToken);
        return profile;
    }
}
