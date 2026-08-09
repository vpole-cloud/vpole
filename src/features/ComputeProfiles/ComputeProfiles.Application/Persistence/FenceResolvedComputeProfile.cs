using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class MongoFenceResolvedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles)
    : IComputeProfileResolutionFence
{
    public Task<Result> FenceAsync(
        ComputeProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            request,
            session,
            cancellationToken);

    public async Task<Result> InvokeAsync(
        ComputeProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTransaction(session);
        if (!ObjectId.TryParse(request.ComputeProfileId, out _) ||
            !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
            !ObjectId.TryParse(request.TenantId, out _) ||
            request.ComputeProfileRevision < 1 ||
            string.IsNullOrWhiteSpace(request.ResourceType.Value))
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
                request),
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
