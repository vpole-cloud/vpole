using MongoDB.Bson;
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
        var requestErrors = ValidateRequest(request);
        if (requestErrors.Count > 0)
            return Failures.ValidationFailure(requestErrors);

        var profile = await dbContext.StorageProfiles
            .Find(item => item.Id == request.StorageProfileId)
            .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
            return Failures.NotFound("Storage profile not found.", request.StorageProfileId);

        var errors = ValidateProfile(profile, request);
        var targetMapping = profile.TargetMappings.SingleOrDefault(mapping =>
            string.Equals(
                mapping.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal));
        if (targetMapping is null)
        {
            errors["StorageProfileId"] =
            [
                "Storage profile is not configured for the selected deployment target."
            ];
        }
        if (errors.Count > 0)
            return Failures.ValidationFailure(errors);

        var storageClassesResult = await storageDiscovery.GetStorageClassNamesAsync(
            request.DeploymentTargetId,
            cancellationToken);
        if (!storageClassesResult.TryGetValue(
                out var knownStorageClassNames,
                out var storageClassFailure))
        {
            return storageClassFailure;
        }
        if (!knownStorageClassNames.Contains(targetMapping!.StorageClassName))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["StorageProfileId"] =
                [
                    "The configured Kubernetes storage class is not available in the selected " +
                    "deployment target."
                ]
            });
        }

        return new ResolvedStorageProfile(
            profile.Id,
            profile.Revision,
            request.DeploymentTargetId,
            profile.DisplayName,
            targetMapping.StorageClassName,
            profile.MediaType,
            profile.PerformanceTier);
    }

    internal static bool AllowsTenant(StorageProfile profile, string tenantId) =>
        profile.TenantAccess.Mode == StorageProfileTenantAccessMode.AllTenants ||
        profile.TenantAccess.TenantIds.Contains(tenantId);

    internal static bool AllowsResourceType(
        StorageProfile profile,
        ResourceType resourceType) =>
        profile.AllowedResourceTypes.Contains(resourceType);

    internal static bool AllowsSize(StorageProfile profile, int sizeGb) =>
        (profile.SizeLimits?.MinSizeGb is null || sizeGb >= profile.SizeLimits.MinSizeGb) &&
        (profile.SizeLimits?.MaxSizeGb is null || sizeGb <= profile.SizeLimits.MaxSizeGb);

    private static Dictionary<string, string[]> ValidateRequest(
        StorageProfileResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ObjectId.TryParse(request.StorageProfileId, out _))
            errors["StorageProfileId"] = ["Storage profile ID must be a MongoDB ObjectId."];
        if (!ObjectId.TryParse(request.DeploymentTargetId, out _))
        {
            errors["DeploymentTargetId"] =
            [
                "Deployment target ID must be a MongoDB ObjectId."
            ];
        }
        if (!ObjectId.TryParse(request.TenantId, out _))
            errors["TenantId"] = ["Tenant ID must be a MongoDB ObjectId."];
        if (!Enum.IsDefined(request.ResourceType))
            errors["ResourceType"] = ["Resource type is invalid."];
        if (request.SizeGb <= 0)
            errors["SizeGb"] = ["Requested storage size must be greater than zero."];
        return errors;
    }

    private static Dictionary<string, string[]> ValidateProfile(
        StorageProfile profile,
        StorageProfileResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!profile.IsEnabled)
            errors["StorageProfileId"] = ["Storage profile is disabled."];
        if (!AllowsTenant(profile, request.TenantId))
        {
            errors["StorageProfileId"] =
            [
                "Storage profile is not available to the active tenant."
            ];
        }
        if (!AllowsResourceType(profile, request.ResourceType))
        {
            errors["StorageProfileId"] =
            [
                $"Storage profile is not available for {request.ResourceType} resources."
            ];
        }
        if (!AllowsSize(profile, request.SizeGb))
            errors["SizeGb"] = ["Requested storage size is outside the profile limits."];
        return errors;
    }
}
