using MongoDB.Driver;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Inventory;

internal sealed class GitOwnershipStatuses(IMongoCollection<ManagedDeclaration> inventory)
{
    public async Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
        string tenantId,
        string kind,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken ct)
    {
        if (resourceIds.Count == 0)
            return new Dictionary<string, GitResourceStatus>();

        var items = await inventory.Find(ManagedDeclarationFilters.Resources(
                tenantId,
                kind,
                resourceIds)).ToListAsync(ct);

        return items.ToDictionary(item => item.ResourceId, ToStatus);
    }

    private static GitResourceStatus ToStatus(ManagedDeclaration item) => new(item.SourceId, item.SourceUrl,
        item.Blocker, item.PendingRemoval,
        item.ToDomain().ConfigurationApplied(item.Blocker is not null));

    public async Task<GitResourceStatus?> StatusAsync(
        string tenantId,
        string kind,
        string resourceId,
        CancellationToken ct)
    {
        var item = await inventory.Find(ManagedDeclarationFilters.Resource(
                tenantId,
                kind,
                resourceId)).FirstOrDefaultAsync(ct);

        return item is null ? null : ToStatus(item);
    }
}
