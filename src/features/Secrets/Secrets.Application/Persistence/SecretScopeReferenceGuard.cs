using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class SecretScopeReferenceGuard(IMongoCollection<Secret> secrets)
    : IIsolationScopeReferenceGuard
{
    public async Task<EnsureIsolationScopeUnreferencedOutcome> EnsureUnreferencedAsync(
        EnsureIsolationScopeUnreferencedRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var exists = await secrets.Find(
            session,
            SecretFilters.Scope(request.TenantId, request.ResourceGroupId, request.IsolationScopeId)).AnyAsync(cancellationToken);
        return exists ? new IsolationScopeReferenced(request.IsolationScopeId) : new IsolationScopeUnreferenced();
    }
}
