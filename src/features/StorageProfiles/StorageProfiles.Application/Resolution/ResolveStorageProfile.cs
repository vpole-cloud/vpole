using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Resolution;

internal sealed class MongoResolveStorageProfile(
    IMongoCollection<StorageProfile> profiles,
    IMongoCollection<StorageProfileRevision> revisions,
    DiscoverKubernetesStorageClassNames discover)
{
    public async Task<ResolveStorageProfileOutcome> InvokeAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        StorageProfileResolution.EnsureValid(request);
        var current = await profiles.Find(StorageProfileFilters.ById(request.StorageProfileId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new StorageProfileResolutionError(new StorageProfileNotFound(request.StorageProfileId));

        var unavailable = StorageProfileResolution.ValidatePolicy(current, request);
        if (unavailable is not null)
            return unavailable.Value;

        var selected = current;
        if (request.Revision is { } revision && revision != current.Revision)
        {
            var historical = await revisions.Find(StorageProfileRevisionFilters.Identity(current.Id, revision))
                .FirstOrDefaultAsync(cancellationToken);
            if (historical is null)
                return new StorageProfileResolutionError(new StorageProfileRevisionUnavailable(current.Id, revision));

            selected = historical.Snapshot;
        }

        return await StorageProfileResolution.ResolveAsync(selected, request, discover, cancellationToken);
    }
}

internal static class StorageProfileResolution
{
    public static StorageProfileResolutionError? ValidatePolicy(
        StorageProfile profile,
        StorageProfileResolutionRequest request)
    {
        if (!profile.IsEnabled)
            return new StorageProfileDisabled(profile.Id);

        if (!MongoLoadAvailableStorageProfiles.AllowsTenant(profile, request.TenantId))
            return new StorageProfileTenantAccessDenied(profile.Id, request.TenantId);

        if (!profile.AllowedResourceTypes.Contains(request.ResourceType.Value))
            return new StorageProfileResourceTypeNotAllowed(profile.Id, request.ResourceType);

        if ((profile.SizeLimits?.MinSizeGb is { } min && request.SizeGb < min) ||
            (profile.SizeLimits?.MaxSizeGb is { } max && request.SizeGb > max))
            return new StorageProfileSizeNotAllowed(profile.Id, request.SizeGb);

        if (!profile.TargetMappings.Any(mapping => mapping.DeploymentTargetId == request.DeploymentTargetId))
            return new StorageProfileDeploymentTargetNotConfigured(profile.Id, request.DeploymentTargetId);

        return null;
    }

    public static async Task<ResolveStorageProfileOutcome> ResolveAsync(
        StorageProfile selected,
        StorageProfileResolutionRequest request,
        DiscoverKubernetesStorageClassNames discover,
        CancellationToken ct)
    {
        var mapping = selected.TargetMappings.SingleOrDefault(item =>
            item.DeploymentTargetId == request.DeploymentTargetId);
        if (mapping is null)
            return new StorageProfileResolutionError(
                new StorageProfileDeploymentTargetNotConfigured(selected.Id, request.DeploymentTargetId));

        var classes = await discover(request.DeploymentTargetId, ct);
        if (classes is KubernetesStorageDiscoveryUnavailable)
            return new StorageProfileResolutionError(
                new StorageProfileDiscoveryUnavailable(request.DeploymentTargetId));
        if (!((IReadOnlySet<string>)classes.Value).Contains(mapping.StorageClassName))
            return new StorageProfileResolutionError(new StorageProfileStorageClassUnavailable(
                selected.Id, request.DeploymentTargetId, mapping.StorageClassName));

        return new ResolvedStorageProfile(selected.Id, selected.Revision, request.DeploymentTargetId,
            selected.DisplayName, mapping.StorageClassName, selected.MediaType, selected.PerformanceTier);
    }

    public static void EnsureValid(StorageProfileResolutionRequest request)
    {
        if (!ObjectId.TryParse(request.StorageProfileId, out _) ||
            !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
            !ObjectId.TryParse(request.TenantId, out _) || string.IsNullOrWhiteSpace(request.ResourceType.Value))
            throw new ArgumentException(
                "Valid profile, tenant, target and resource type are required.", nameof(request));
        if (request.SizeGb <= 0 || request.Revision is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Size and explicit revision must be positive.");
    }
}
