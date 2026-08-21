using Pole.Features.ComputeProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class ResolveComputeProfilesLegacyAdapter(
    ResolveComputeProfiles resolveComputeProfiles)
{
    public async Task<Result<IReadOnlyList<ResolvedComputeProfile>>> InvokeAsync(
        ComputeProfileBatchResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await resolveComputeProfiles(request, cancellationToken);
        return result switch
        {
            Ok<IReadOnlyList<ResolvedComputeProfile>> success =>
                new Success<IReadOnlyList<ResolvedComputeProfile>>(success.Value),
            Err<ComputeProfileResolutionError> error =>
                ComputeProfileResolutionLegacyFailures.ToFailure(error.Value)
        };
    }
}
