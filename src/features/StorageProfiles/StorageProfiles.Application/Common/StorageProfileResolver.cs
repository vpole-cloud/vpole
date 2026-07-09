using MongoDB.Driver;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed class StorageProfileResolver(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery)
    : IStorageProfileResolver
{
    public async Task<Result<ResolvedStorageProfile>> ResolveAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.StorageProfileId))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["StorageProfileId"] = ["Storage profile is required."]
            });
        }

        var profile = await dbContext.StorageProfiles
            .Find(x => x.Id == request.StorageProfileId)
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
            return Failures.NotFound("Storage profile not found.", request.StorageProfileId);

        var errors = await ValidateProfileAsync(profile, request, cancellationToken);
        if (errors.Count > 0)
            return Failures.ValidationFailure(errors);

        var knownStorageClassNames = await storageDiscovery.GetStorageClassNamesAsync(cancellationToken);
        var storageClassName = profile.StorageClassNames.First(knownStorageClassNames.Contains);

        return new ResolvedStorageProfile(
            profile.Id,
            profile.DisplayName,
            storageClassName,
            profile.MediaType,
            profile.PerformanceTier);
    }

    internal static bool AllowsTenant(StorageProfile profile, string tenantId) =>
        profile.TenantAccess.Mode == StorageProfileTenantAccessMode.AllTenants ||
        profile.TenantAccess.TenantIds.Contains(tenantId);

    internal static bool AllowsResourceType(StorageProfile profile, ResourceType resourceType) =>
        profile.AllowedResourceTypes.Contains(resourceType);

    internal static bool AllowsSize(StorageProfile profile, int sizeGb) =>
        (profile.SizeLimits?.MinSizeGb is null || sizeGb >= profile.SizeLimits.MinSizeGb) &&
        (profile.SizeLimits?.MaxSizeGb is null || sizeGb <= profile.SizeLimits.MaxSizeGb);

    private async Task<Dictionary<string, string[]>> ValidateProfileAsync(
        StorageProfile profile,
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (!profile.IsEnabled)
            errors["StorageProfileId"] = ["Storage profile is disabled."];

        if (!AllowsTenant(profile, request.TenantId))
            errors["StorageProfileId"] = ["Storage profile is not available to the active tenant."];

        if (!AllowsResourceType(profile, request.ResourceType))
            errors["StorageProfileId"] = [$"Storage profile is not available for {request.ResourceType} resources."];

        if (!AllowsSize(profile, request.SizeGb))
            errors["SizeGb"] = ["Requested storage size is outside the profile limits."];

        var knownStorageClassNames = await storageDiscovery.GetStorageClassNamesAsync(cancellationToken);
        if (!profile.StorageClassNames.Any(knownStorageClassNames.Contains))
            errors["StorageProfileId"] = ["Storage profile has no known backing Kubernetes storage class."];

        return errors;
    }
}
