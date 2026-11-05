using Pole.Features.StorageProfiles.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Application.Catalog;

public sealed record StorageProfileAvailabilityQuery(
    string TenantId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType);

public delegate Task<IReadOnlyCollection<StorageProfile>> LoadAvailableStorageProfiles(
    StorageProfileAvailabilityQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadAvailableStorageProfiles(
    IMongoCollection<StorageProfile> storageProfiles)
{
    public async Task<IReadOnlyCollection<StorageProfile>> InvokeAsync(
        StorageProfileAvailabilityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var profiles = await storageProfiles
            .Find(StorageProfileFilters.AvailableForTarget(query.DeploymentTargetId))
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        return profiles
            .Where(profile => AllowsTenant(profile, query.TenantId))
            .Where(profile => profile.AllowedResourceTypes.Contains(query.ResourceType.Value))
            .ToArray();
    }

    public static bool AllowsTenant(StorageProfile profile, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.TenantAccess.Mode == StorageProfileTenantAccessMode.AllTenants ||
               profile.TenantAccess.TenantIds.Contains(tenantId);
    }
}
