using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class MongoFenceResolvedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles)
    : IComputeProfileResolutionFence
{
    public Task<Result> FenceAsync(
        ResolvedComputeProfile compute,
        string tenantId,
        ResourceTypeId resourceType,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            compute,
            tenantId,
            resourceType,
            session,
            cancellationToken);

    public async Task<Result> InvokeAsync(
        ResolvedComputeProfile resolved,
        string tenantId,
        ResourceTypeId resourceType,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        EnsureTransaction(session);
        if (!ObjectId.TryParse(resolved.ComputeProfileId, out _) ||
            !ObjectId.TryParse(resolved.DeploymentTargetId, out _) ||
            !ObjectId.TryParse(tenantId, out _) ||
            resolved.ComputeProfileRevision < 1 ||
            string.IsNullOrWhiteSpace(resourceType.Value))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["ComputeProfileId"] =
                    ["A valid resolved compute profile is required."]
            });
        }

        var fenced = await computeProfiles.UpdateOneAsync(
            session,
            ComputeProfileFilters.AvailableForResolutionFence(
                resolved,
                tenantId,
                resourceType),
            Builders<ComputeProfile>.Update.Inc(
                profile => profile.ResolutionFence,
                1),
            cancellationToken: cancellationToken);
        return fenced.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The compute profile mapping changed or is no longer available.");
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Compute-profile resolution fencing must run inside the logical resource transaction.");
        }
    }
}
