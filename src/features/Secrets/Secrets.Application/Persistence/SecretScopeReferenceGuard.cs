using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;
namespace Pole.Features.Secrets.Application;

public sealed class SecretScopeReferenceGuard(IMongoCollection<Secret> secrets) : IIsolationScopeReferenceGuard
{
    public async Task<Result<Unit, IsolationScopeReferenced>> EnsureUnreferencedAsync(
        string tenantId, string resourceGroupId, string isolationScopeId, IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var exists = await secrets.Find(session, SecretFilters.Scope(tenantId, resourceGroupId, isolationScopeId)).AnyAsync(cancellationToken);
        return exists ? new Err<IsolationScopeReferenced>(new(isolationScopeId)) : new Ok<Unit>(Unit.Value);
    }
}
