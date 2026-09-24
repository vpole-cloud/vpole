using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Persistence.Reads;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence.Resolution;

internal sealed class MongoResolveStorageProfile(
    IMongoCollection<StorageProfile> storageProfiles,
    DiscoverKubernetesStorageClassNames discoverStorageClassNames)
{
    public async Task<ResolveStorageProfileOutcome> InvokeAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValidRequest(request);

        var profile = await storageProfiles
            .Find(StorageProfileFilters.ById(request.StorageProfileId))
            .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return new StorageProfileResolutionError(
                new StorageProfileNotFound(request.StorageProfileId));
        }
        if (!profile.IsEnabled)
        {
            return new StorageProfileResolutionError(
                new StorageProfileDisabled(profile.Id));
        }
        if (!MongoLoadAvailableStorageProfiles.AllowsTenant(profile, request.TenantId))
        {
            return new StorageProfileResolutionError(
                new StorageProfileTenantAccessDenied(profile.Id, request.TenantId));
        }
        if (!profile.AllowedResourceTypes.Contains(request.ResourceType.Value))
        {
            return new StorageProfileResolutionError(
                new StorageProfileResourceTypeNotAllowed(profile.Id, request.ResourceType));
        }
        if (!AllowsSize(profile, request.SizeGb))
        {
            return new StorageProfileResolutionError(
                new StorageProfileSizeNotAllowed(profile.Id, request.SizeGb));
        }

        var targetMapping = profile.TargetMappings.SingleOrDefault(mapping =>
            string.Equals(
                mapping.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal));
        if (targetMapping is null)
        {
            return new StorageProfileResolutionError(
                new StorageProfileDeploymentTargetNotConfigured(
                    profile.Id,
                    request.DeploymentTargetId));
        }

        var storageClasses = await discoverStorageClassNames(
            request.DeploymentTargetId,
            cancellationToken);
        if (storageClasses is KubernetesStorageDiscoveryUnavailable)
        {
            return new StorageProfileResolutionError(
                new StorageProfileDiscoveryUnavailable(request.DeploymentTargetId));
        }
        var names = (IReadOnlySet<string>)storageClasses.Value;
        if (!names.Contains(targetMapping.StorageClassName))
        {
            return new StorageProfileResolutionError(
                new StorageProfileStorageClassUnavailable(
                    profile.Id,
                    request.DeploymentTargetId,
                    targetMapping.StorageClassName));
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

    private static bool AllowsSize(StorageProfile profile, int sizeGb) =>
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
        if (string.IsNullOrWhiteSpace(request.ResourceType.Value))
            throw new ArgumentException("Resource type is invalid.", nameof(request));
        if (request.SizeGb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Requested storage size must be greater than zero.");
        }
    }
}
