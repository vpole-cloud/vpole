using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentComputeProfileReferenceGuard(
    IResourceDeploymentDbContext dbContext)
    : IResourceDeploymentComputeProfileReferenceGuard
{
    public async Task<Result> EnsureUnreferencedAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computeProfileId);
        ArgumentNullException.ThrowIfNull(session);

        var referenced = await dbContext.ResourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ReferencesComputeProfile(computeProfileId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return referenced
            ? Failures.Conflict(
                "The compute profile is still referenced by a resource deployment.")
            : Result.Success();
    }
}
