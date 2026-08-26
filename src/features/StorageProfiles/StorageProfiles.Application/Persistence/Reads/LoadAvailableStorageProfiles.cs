using MongoDB.Driver;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence.Reads;

public sealed record StorageProfileAvailabilityQuery(
    string TenantId,
    string DeploymentTargetId,
    ResourceType ResourceType);

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
            .Where(profile => profile.AllowedResourceTypes.Contains(query.ResourceType))
            .ToArray();
    }

    public static bool AllowsTenant(StorageProfile profile, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.TenantAccess.Mode == StorageProfileTenantAccessMode.AllTenants ||
               profile.TenantAccess.TenantIds.Contains(tenantId);
    }
}
