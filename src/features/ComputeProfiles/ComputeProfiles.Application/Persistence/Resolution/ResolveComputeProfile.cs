using Pole.Features.ComputeProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Resolution;

internal sealed class MongoResolveComputeProfile(
    MongoResolveComputeProfiles batchResolver)
{
    public async Task<Result<
        ResolvedComputeProfile,
        ComputeProfileResolutionError>> InvokeAsync(
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
            Ok<IReadOnlyList<ResolvedComputeProfile>> profiles =>
                new Ok<ResolvedComputeProfile>(profiles.Value.Single()),
            Err<ComputeProfileResolutionError> error => error
        };
    }
}
