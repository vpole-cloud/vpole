using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.IsolationScopes;

public delegate Task<IReadOnlyList<IsolationScope>> LoadIsolationScopes(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadIsolationScopes(
    IMongoCollection<IsolationScope> isolationScopes
)
{
    public async Task<IReadOnlyList<IsolationScope>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default) => await isolationScopes
        .Find(IsolationScopeFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
        .SortByDescending(scope => scope.IsDefault)
        .ThenBy(scope => scope.NormalizedName)
        .ToListAsync(cancellationToken);
}
