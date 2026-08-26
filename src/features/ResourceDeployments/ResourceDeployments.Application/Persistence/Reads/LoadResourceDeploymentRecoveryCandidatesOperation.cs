using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

public delegate Task<IReadOnlyList<string>> LoadResourceDeploymentRecoveryCandidates(
    DateTime now,
    DateTime stalePendingBefore,
    int batchSize,
    CancellationToken cancellationToken = default);

internal sealed class LoadResourceDeploymentRecoveryCandidatesOperation(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<IReadOnlyList<string>> InvokeAsync(
        DateTime now,
        DateTime stalePendingBefore,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await resourceDeployments
            .Find(ResourceDeploymentFilters.ForRecoveryCandidates(
                now,
                stalePendingBefore))
            .SortBy(deployment => deployment.UpdatedAtUtc)
            .ThenBy(deployment => deployment.Id)
            .Limit(batchSize)
            .Project(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
}
