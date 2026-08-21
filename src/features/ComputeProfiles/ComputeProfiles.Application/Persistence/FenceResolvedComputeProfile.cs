using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class MongoFenceResolvedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<Result<Unit, ComputeProfileResolutionFenceRejected>> InvokeAsync(
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        EnsureTransaction(session);
        EnsureValid(requests);

        var profiles = await computeProfiles
            .Find(
                session,
                ComputeProfileFilters.ByIds(
                    requests
                        .Select(request => request.ComputeProfileId)
                        .Distinct(StringComparer.Ordinal)))
            .ToListAsync(cancellationToken);
        var profilesById = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);

        foreach (var request in requests)
        {
            if (!profilesById.TryGetValue(request.ComputeProfileId, out var profile) ||
                MongoResolveComputeProfiles.ValidateAvailability(
                    profile,
                    request.TenantId,
                    request.ResourceType,
                    request.DeploymentTargetId) is not null ||
                profile.Revision != request.ComputeProfileRevision ||
                profile.ResolutionFence == long.MaxValue)
            {
                return Rejected(requests);
            }
        }

        var firstRequest = requests[0];
        var writes = profiles
            .Where(profile => requests.Any(request =>
                string.Equals(
                    request.ComputeProfileId,
                    profile.Id,
                    StringComparison.Ordinal)))
            .Select(profile => new UpdateOneModel<ComputeProfile>(
                ComputeProfileFilters.AvailableAtSnapshot(
                    profile,
                    firstRequest.TenantId,
                    firstRequest.ResourceType),
                Builders<ComputeProfile>.Update.Inc(
                    candidate => candidate.ResolutionFence,
                    1)))
            .ToArray();
        var fenced = await computeProfiles.BulkWriteAsync(
            session,
            writes,
            new BulkWriteOptions { IsOrdered = true },
            cancellationToken);
        return fenced.ModifiedCount == writes.Length
            ? new Ok<Unit>(Unit.Value)
            : Rejected(requests);
    }

    private static void EnsureValid(
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests)
    {
        if (requests.Count == 0)
        {
            throw new ArgumentException(
                "At least one resolved compute profile is required.",
                nameof(requests));
        }

        var first = requests[0];
        if (requests.Any(request =>
                !string.Equals(request.TenantId, first.TenantId, StringComparison.Ordinal) ||
                !string.Equals(
                    request.ResourceType.Value,
                    first.ResourceType.Value,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "All compute-profile fences in a batch must have one tenant and resource type.",
                nameof(requests));
        }

        if (requests.Any(request =>
                !ObjectId.TryParse(request.ComputeProfileId, out _) ||
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                !ObjectId.TryParse(request.TenantId, out _) ||
                request.ComputeProfileRevision < 1 ||
                string.IsNullOrWhiteSpace(request.ResourceType.Value)))
        {
            throw new ArgumentException(
                "A valid resolved compute profile is required.",
                nameof(requests));
        }

        if (requests
            .GroupBy(request => request.ComputeProfileId, StringComparer.Ordinal)
            .Any(group => group.Select(request => request.ComputeProfileRevision).Distinct().Skip(1).Any()))
        {
            throw new ArgumentException(
                "A compute profile must have one revision within a fence batch.",
                nameof(requests));
        }
    }

    private static Result<Unit, ComputeProfileResolutionFenceRejected> Rejected(
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests) =>
        new Err<ComputeProfileResolutionFenceRejected>(
            new ComputeProfileResolutionFenceRejected(
                requests
                    .Select(request => request.ComputeProfileId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));

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
