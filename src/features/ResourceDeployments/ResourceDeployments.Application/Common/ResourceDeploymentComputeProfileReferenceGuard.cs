using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal sealed class ResourceDeploymentComputeProfileReferenceGuard(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<Unit, ComputeProfileReferencedByResourceDeployment>>
        EnsureUnreferencedAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computeProfileId);
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment reference checks must run inside the logical resource transaction.");
        }

        var referenced = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ReferencesComputeProfile(computeProfileId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return referenced
            ? new Err<ComputeProfileReferencedByResourceDeployment>(
                new ComputeProfileReferencedByResourceDeployment(computeProfileId))
            : new Ok<Unit>(Unit.Value);
    }
}
