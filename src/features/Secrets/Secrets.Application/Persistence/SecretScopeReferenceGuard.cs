using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class SecretScopeReferenceGuard(IMongoCollection<Secret> secrets)
    : IIsolationScopeReferenceGuard
{
    public async Task<EnsureIsolationScopeUnreferencedOutcome> EnsureUnreferencedAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var exists = await secrets.Find(
            session,
            SecretFilters.Scope(tenantId, resourceGroupId, isolationScopeId)).AnyAsync(cancellationToken);
        return exists ? new IsolationScopeReferenced(isolationScopeId) : new IsolationScopeUnreferenced();
    }
}
