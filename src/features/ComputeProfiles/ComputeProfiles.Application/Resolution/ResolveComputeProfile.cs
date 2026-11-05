using Pole.Features.ComputeProfiles.Contracts;

namespace Pole.Features.ComputeProfiles.Application.Resolution;

internal sealed class MongoResolveComputeProfile(
    MongoResolveComputeProfiles batchResolver)
{
    public async Task<ResolveComputeProfileOutcome> InvokeAsync(
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
        return result switch
        {
            IReadOnlyList<ResolvedComputeProfile> profiles =>
                profiles.Single(),
            ComputeProfileResolutionError error => error
        };
    }
}
