using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public sealed record ComputeProfileAvailabilityQuery(
    string TenantId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType);

public delegate Task<IReadOnlyList<ComputeProfile>> LoadAvailableComputeProfiles(
    ComputeProfileAvailabilityQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadAvailableComputeProfiles(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<IReadOnlyList<ComputeProfile>> InvokeAsync(
        ComputeProfileAvailabilityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var candidates = await computeProfiles
            .Find(ComputeProfileFilters.AvailableForTarget(query.DeploymentTargetId))
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(profile => profile.AllowedResourceTypes.Contains(query.ResourceType.Value))
            .Where(profile => AllowsTenant(profile.TenantAccess, query.TenantId))
            .ToArray();
    }

    internal static bool AllowsTenant(
        ComputeProfileTenantAccess access,
        string tenantId) =>
        access switch
        {
            AllTenantsComputeProfileAccess => true,
            SelectedTenantsComputeProfileAccess selected =>
                selected.TenantIds.Contains(tenantId),
            _ => false
        };
}
