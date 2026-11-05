using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;

namespace Pole.Features.GitProviders.Application.Repositories;

public sealed record SourceRepositoryDeleted;

public union PersistDeletedSourceRepositoryOutcome(
    SourceRepositoryDeleted,
    SourceRepositoryNotFound,
    SourceRepositoryHasBuildConfiguration,
    SourceRepositoryGitOwned);

public sealed record RemoveSourceRepositoryRequest(
    string TenantId,
    string SourceRepositoryId,
    GitLifecycleAuthority? Lifecycle = null);

public delegate Task<PersistDeletedSourceRepositoryOutcome> DeleteSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceRepository(
    IMongoCollection<SourceRepository> repositories,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    EnsureNoSourceRepositoryBuildConfiguration ensureNoConfiguration,
    IGitOpsMutations git)
{
    public Task<PersistDeletedSourceRepositoryOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistDeletedSourceRepositoryOutcome>(async (tx, ct) =>
        {
            var outcome = await ExecuteAsync(new(tenantId, sourceRepositoryId), tx.Session, ct);
            return outcome is SourceRepositoryDeleted ? tx.Commit(outcome) : tx.Abort(outcome);
        }, cancellationToken);

    internal async Task<PersistDeletedSourceRepositoryOutcome> ExecuteAsync(
        RemoveSourceRepositoryRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Source repository deletion requires a caller-owned transaction.");
        var current = await repositories.Find(session,
            GitProviderFilters.SourceRepositoryByTenantAndId(request.TenantId, request.SourceRepositoryId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new SourceRepositoryNotFound(request.SourceRepositoryId);
        if (!await git.AuthorizeAsync(new(request.TenantId, "SourceRepository", current.Id,
                Lifecycle: request.Lifecycle,
                LifecycleEffect: GitLifecycleMutationEffect.Remove), session, ct))
            return new SourceRepositoryGitOwned(current.Id);

        var repository = await repositories.FindOneAndUpdateAsync(session,
            GitProviderFilters.SourceRepositoryAtPlanningVersion(current),
            Builders<SourceRepository>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<SourceRepository> { ReturnDocument = ReturnDocument.After }, ct);
        if (repository is null)
            return new SourceRepositoryNotFound(current.Id);
        var guard = await ensureNoConfiguration(new(request.TenantId, current.Id), session, ct);
        if (guard is SourceBuildSourceRepositoryConfigurationExists)
            return new SourceRepositoryHasBuildConfiguration(current.Id);

        var removed = await repositories.DeleteOneAsync(session,
            GitProviderFilters.SourceRepositoryAtPlanningVersion(repository), cancellationToken: ct);
        if (removed.DeletedCount != 1)
            return new SourceRepositoryNotFound(current.Id);
        await outbox(session, [OutboxWrite.Create(repository.ToChangedEvent())], ct);
        return new SourceRepositoryDeleted();
    }
}
