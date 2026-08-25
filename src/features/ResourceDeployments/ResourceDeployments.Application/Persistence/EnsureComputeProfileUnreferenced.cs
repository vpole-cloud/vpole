using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class MongoEnsureComputeProfileUnreferenced(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<Unit, ComputeProfileReferencedByResourceDeployment>> InvokeAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computeProfileId);
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment reference checks");

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
