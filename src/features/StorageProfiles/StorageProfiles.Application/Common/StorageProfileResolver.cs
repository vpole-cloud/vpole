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
{
    public async Task<Result<ResolvedStorageProfile, StorageProfileResolutionError>> InvokeAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValidRequest(request);

        var profile = await dbContext.StorageProfiles
            .Find(item => item.Id == request.StorageProfileId)
            .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileNotFound(request.StorageProfileId));
        }

        if (!profile.IsEnabled)
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileDisabled(profile.Id));
        }
        if (!AllowsTenant(profile, request.TenantId))
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileTenantAccessDenied(profile.Id, request.TenantId));
        }
        if (!AllowsResourceType(profile, request.ResourceType))
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileResourceTypeNotAllowed(profile.Id, request.ResourceType));
        }
        if (!AllowsSize(profile, request.SizeGb))
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileSizeNotAllowed(profile.Id, request.SizeGb));
        }

        var targetMapping = profile.TargetMappings.SingleOrDefault(mapping =>
            string.Equals(
                mapping.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal));
        if (targetMapping is null)
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileDeploymentTargetNotConfigured(
                    profile.Id,
                    request.DeploymentTargetId));
        }

        var storageClassesResult = await storageDiscovery.GetStorageClassNamesAsync(
            request.DeploymentTargetId,
            cancellationToken);
        if (!storageClassesResult.TryGetValue(out var knownStorageClassNames, out _))
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileDiscoveryUnavailable(request.DeploymentTargetId));
        }
        if (!knownStorageClassNames.Contains(targetMapping!.StorageClassName))
        {
            return new Err<StorageProfileResolutionError>(
                new StorageProfileStorageClassUnavailable(
                    profile.Id,
                    request.DeploymentTargetId,
                    targetMapping.StorageClassName));
        }

        return new Ok<ResolvedStorageProfile>(new ResolvedStorageProfile(
            profile.Id,
            profile.Revision,
            request.DeploymentTargetId,
            profile.DisplayName,
            targetMapping.StorageClassName,
            profile.MediaType,
            profile.PerformanceTier));
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

    private static void EnsureValidRequest(StorageProfileResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ObjectId.TryParse(request.StorageProfileId, out _))
        {
            throw new ArgumentException(
                "Storage profile ID must be a MongoDB ObjectId.",
                nameof(request));
        }
        if (!ObjectId.TryParse(request.DeploymentTargetId, out _))
        {
            throw new ArgumentException(
                "Deployment target ID must be a MongoDB ObjectId.",
                nameof(request));
        }
        if (!ObjectId.TryParse(request.TenantId, out _))
        {
            throw new ArgumentException(
                "Tenant ID must be a MongoDB ObjectId.",
                nameof(request));
        }
        if (!Enum.IsDefined(request.ResourceType))
            throw new ArgumentOutOfRangeException(nameof(request), "Resource type is invalid.");
        if (request.SizeGb <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Requested storage size must be greater than zero.");
    }
}
