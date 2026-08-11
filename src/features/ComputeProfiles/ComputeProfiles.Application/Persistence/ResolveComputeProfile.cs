using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class MongoResolveComputeProfile(
    MongoResolveComputeProfiles batchResolver)
{
    public async Task<Result<ResolvedComputeProfile>> InvokeAsync(
        ComputeProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await batchResolver.InvokeAsync(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                request.ResourceType,
                [new ComputeProfileTargetResolutionRequest(
                    request.DeploymentTargetId,
                    request.ComputeProfileId)]),
            cancellationToken);
        if (!result.TryGetValue(out var profiles, out var failure))
            return failure;

        return profiles.Single();
    }
}
