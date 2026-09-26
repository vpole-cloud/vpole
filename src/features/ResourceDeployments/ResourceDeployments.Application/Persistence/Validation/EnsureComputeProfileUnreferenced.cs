using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoEnsureComputeProfileUnreferenced(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<EnsureComputeProfileUnreferencedOutcome> InvokeAsync(
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
            ? new ComputeProfileReferencedByResourceDeployment(computeProfileId)
            : new ComputeProfileUnreferenced();
    }
}
