using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<bool> TryPersistSourceBuildJob(
    SourceBuildJob current,
    SourceBuildJob next,
    CancellationToken cancellationToken = default);

internal sealed class MongoTryPersistSourceBuildJob(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<bool> InvokeAsync(
        SourceBuildJob current,
        SourceBuildJob next,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(current.Id, next.Id, StringComparison.Ordinal) ||
            !string.Equals(current.TenantId, next.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A source build job replacement must preserve build-job and tenant identity.");
        }

        var updated = await jobs.ReplaceOneAsync(
            SourceBuildJobFilters.AtUpdatedAt(current),
            next,
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1;
    }
}
