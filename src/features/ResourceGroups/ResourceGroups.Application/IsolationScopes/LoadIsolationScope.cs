using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.IsolationScopes;

public delegate Task<IsolationScope?> LoadIsolationScope(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadIsolationScope(IMongoCollection<IsolationScope> isolationScopes)
{
    public async Task<IsolationScope?> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default) =>
        await isolationScopes
            .Find(
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                    tenantId,
                    resourceGroupId,
                    isolationScopeId))
            .FirstOrDefaultAsync(cancellationToken);
}
