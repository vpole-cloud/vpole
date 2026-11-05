using Pole.Features.GitProviders.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.GitProviders.Application.Repositories;

internal sealed class MongoSourceRepositoryReferences(
    IMongoCollection<SourceRepository> repositories,
    IMongoCollection<GitProviderConnection> connections,
    FenceTenantIdentity fenceTenant)
{
    public async Task<FindNamedSourceRepositoryOutcome> FindNamedAsync(
        NamedSourceRepositoryRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var repository = await repositories.Find(session,
                GitProviderFilters.SourceRepositoryByTenantAndName(request.TenantId, request.TechnicalName))
            .FirstOrDefaultAsync(cancellationToken);
        return repository is null
            ? new NamedSourceRepositoryNotFound(request.TechnicalName)
            : new NamedSourceRepositoryFound(repository.ToBuildSnapshot());
    }

    public async Task<FindSourceRepositoryBuildOutcome> LoadAsync(
        SourceRepositoryReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var repository = await repositories.Find(session,
                GitProviderFilters.SourceRepositoryByTenantAndId(request.TenantId, request.SourceRepositoryId))
            .FirstOrDefaultAsync(cancellationToken);
        return repository is null
            ? new SourceRepositoryBuildNotFound(request.SourceRepositoryId)
            : repository.ToBuildSnapshot();
    }

    public async Task<FenceSourceRepositoryOutcome> FenceAsync(
        FenceSourceRepositoryRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Repository reference fencing requires a Mongo transaction.");

        var filter = GitProviderFilters.SourceRepositoryAtRevision(
            request.TenantId, request.SourceRepositoryId, request.ExpectedRevision);
        var repository = await repositories.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (repository is null ||
            await fenceTenant(new(request.TenantId), session, cancellationToken) is TenantIdentityNotFound)
            return new SourceRepositoryReferenceConflict(request.SourceRepositoryId);

        var connectionFilter = GitProviderFilters.GitHubConnectionByTenantAndId(
            request.TenantId, repository.ConnectionId);
        var connection = await connections.UpdateOneAsync(session, connectionFilter,
            Builders<GitProviderConnection>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (connection.MatchedCount != 1)
            return new SourceRepositoryReferenceConflict(request.SourceRepositoryId);

        var result = await repositories.UpdateOneAsync(session, filter,
            Builders<SourceRepository>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return result.MatchedCount == 1
            ? new SourceRepositoryFenced()
            : new SourceRepositoryReferenceConflict(request.SourceRepositoryId);
    }
}
