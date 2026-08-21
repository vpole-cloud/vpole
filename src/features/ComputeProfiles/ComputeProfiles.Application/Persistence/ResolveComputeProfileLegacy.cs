using Pole.Features.ComputeProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class ResolveComputeProfileLegacyAdapter(
    ResolveComputeProfile resolveComputeProfile)
{
    public async Task<Result<ResolvedComputeProfile>> InvokeAsync(
        ComputeProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await resolveComputeProfile(request, cancellationToken);
        return result switch
        {
            Ok<ResolvedComputeProfile> success =>
                new Success<ResolvedComputeProfile>(success.Value),
            Err<ComputeProfileResolutionError> error =>
                ComputeProfileResolutionLegacyFailures.ToFailure(error.Value)
        };
    }
}
