using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Placements;
using Pole.Features.ResourceDeployments.Application.Planning;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

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
