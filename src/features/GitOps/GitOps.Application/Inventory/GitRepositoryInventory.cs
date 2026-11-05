using Pole.Features.GitOps.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Inventory;

public sealed record ReadRepositoryInventory(
    string TenantId,
    string SourceId,
    int Page,
    int PageSize);

public sealed class GitRepositoryInventory(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<ManagedDeclaration> inventory,
    DeclarativeProviderRegistry providers,
    MongoRunTransaction transactions)
{
    public Task<ListRepositoryResourcesOutcome> ReadAsync(ReadRepositoryInventory request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ListRepositoryResourcesOutcome>(async (tx, token) =>
        {
            if (!await sources.Find(tx.Session,
                    GitRepositoryFilters.VisibleSource(request.TenantId, request.SourceId)).AnyAsync(token))
                return tx.Abort(new GitSourceNotFound());

            var state = await states.Find(tx.Session, GitRepositoryFilters.State(request.TenantId, request.SourceId))
                .SingleAsync(token);
            var items = await inventory.Find(tx.Session,
                    ManagedDeclarationFilters.SourceInventory(request.TenantId, request.SourceId))
                .SortBy(item => item.Kind).ThenBy(item => item.Name).ThenBy(item => item.ResourceId)
                .Skip((request.Page - 1) * request.PageSize).Limit(request.PageSize + 1).ToListAsync(token);
            var page = new RepositoryResourcePage(items.Take(request.PageSize)
                .Select(item => new RepositoryResourceView(
                item.Kind, item.Name, item.ResourceId,
                providers.Find(item.Kind) is IDeclarativeResourceViewProvider view
                    ? view.ResourceType.Value : null,
                item.Materialization, item.PendingRemoval, item.Blocker, item.LastRunId)).ToArray(),
                request.Page, request.PageSize, items.Count > request.PageSize, state.InventoryRevision);
            return tx.Commit(page);
        }, ct);
}
