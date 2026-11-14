using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Backups.Application.Destinations;

internal sealed class BackupDestinationScopeGuard(
    IMongoCollection<BackupDestination> destinations
) : IIsolationScopeReferenceGuard
{
    public async Task<EnsureIsolationScopeUnreferencedOutcome> EnsureUnreferencedAsync(
        EnsureIsolationScopeUnreferencedRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var filter = BackupDestinationFilters.Scope(
            request.TenantId,
            request.ResourceGroupId,
            request.IsolationScopeId);
        return await destinations.Find(session, filter).AnyAsync(cancellationToken)
            ? new IsolationScopeReferenced(request.IsolationScopeId)
            : new IsolationScopeUnreferenced();
    }
}
