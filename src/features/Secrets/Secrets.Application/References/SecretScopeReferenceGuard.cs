using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.References;

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
            SecretFilters.Scope(request.TenantId, request.ResourceGroupId,
                request.IsolationScopeId)).AnyAsync(cancellationToken);
        return exists ? new IsolationScopeReferenced(request.IsolationScopeId) : new IsolationScopeUnreferenced();
    }
}
