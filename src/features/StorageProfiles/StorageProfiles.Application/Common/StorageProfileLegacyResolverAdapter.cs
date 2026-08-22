using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Common;

/// <summary>
/// Compatibility adapter for the remaining consumers of the pre-union storage
/// profile contract. Remove it once those consumers use
/// <see cref="ResolveStorageProfile"/> directly.
/// </summary>
public sealed class StorageProfileLegacyResolverAdapter(
    ResolveStorageProfile resolveStorageProfile)
    : IStorageProfileResolver
{
    public async Task<Result<ResolvedStorageProfile>> ResolveAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await resolveStorageProfile(request, cancellationToken);
        return result switch
        {
            Ok<ResolvedStorageProfile> success =>
                new Success<ResolvedStorageProfile>(success.Value),
            Err<StorageProfileResolutionError> error => ToLegacyFailure(error.Value)
        };
    }

    private static Failure ToLegacyFailure(
        StorageProfileResolutionError error) => error switch
    {
        StorageProfileNotFound notFound =>
            Failures.NotFound("Storage profile not found.", notFound.StorageProfileId),
        StorageProfileDisabled or
            StorageProfileTenantAccessDenied or
            StorageProfileResourceTypeNotAllowed or
            StorageProfileSizeNotAllowed or
            StorageProfileDeploymentTargetNotConfigured or
            StorageProfileStorageClassUnavailable => Failures.ValidationFailure(
                new Dictionary<string, string[]>
                {
                    ["StorageProfileId"] = ["Storage profile is unavailable for this workload."]
                }),
        StorageProfileDiscoveryUnavailable => Failures.ExternalService(
            "Unable to query Kubernetes storage classes.",
            "Kubernetes",
            "Storage profile discovery unavailable.")
    };
}
