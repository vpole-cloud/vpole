using Pole.Features.GitOps.Domain;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class ManualSnapshotSourceFilters
{
    public const string CreationKeyIndex = "UX_ManualSnapshotSources_Tenant_CreationKey";

    public static FilterDefinition<ManualSnapshotSource> Identity(string tenant, string id) =>
        Builders<ManualSnapshotSource>.Filter.Eq(
            item => item.TenantId,
            tenant) & Builders<ManualSnapshotSource>.Filter.Eq(
            item => item.Id,
            id);

    public static FilterDefinition<ManualSnapshotSource> Creation(string tenant, string key) =>
        Builders<ManualSnapshotSource>.Filter.Eq(
            item => item.TenantId,
            tenant) & Builders<ManualSnapshotSource>.Filter.Eq(
            item => item.CreationKey,
            key);
}
