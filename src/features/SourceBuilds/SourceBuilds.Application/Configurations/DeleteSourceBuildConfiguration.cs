using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

public sealed record SourceBuildConfigurationRemovalRecorded;

public union PersistDeletedSourceBuildConfigurationOutcome(
    SourceBuildConfigurationRemovalRecorded,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationHasActiveJobs,
    SourceBuildConfigurationInUse,
    SourceBuildConfigurationGitOwned);

public sealed record RemoveSourceBuildConfigurationRequest(
    string TenantId,
    string ConfigurationId,
    GitLifecycleAuthority? Lifecycle = null
);

public delegate Task<PersistDeletedSourceBuildConfigurationOutcome> DeleteSourceBuildConfiguration(
    string tenantId,
    string configurationId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    LoadSourceBuildTarget loadTarget,
    StoreOutboxWrites outbox
)
{
    public Task<PersistDeletedSourceBuildConfigurationOutcome> InvokeAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistDeletedSourceBuildConfigurationOutcome>(
            async (transaction, ct) =>
            {
                var outcome = await ExecuteAsync(
                    new RemoveSourceBuildConfigurationRequest(tenantId, configurationId),
                    transaction.Session,
                    ct);
                return outcome is SourceBuildConfigurationRemovalRecorded
                    ? transaction.Commit(outcome)
                    : transaction.Abort(outcome);
            },
            cancellationToken);

    internal async Task<PersistDeletedSourceBuildConfigurationOutcome> ExecuteAsync(
        RemoveSourceBuildConfigurationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Build configuration deletion requires a caller-owned transaction.");

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    request.TenantId,
                    "SourceBuildConfiguration",
                    request.ConfigurationId,
                    Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session,
                ct))
            return new SourceBuildConfigurationGitOwned(request.ConfigurationId);

        // Queue and promotion write this same root before inspecting related documents.
        var fenced = await configurations.UpdateOneAsync(
            session,
            SourceBuildConfigurationFilters.ById(request.TenantId, request.ConfigurationId),
            Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: ct);
        if (fenced.MatchedCount != 1)
            return new SourceBuildConfigurationNotFound(request.ConfigurationId);

        var hasActiveJobs = await jobs.Find(
                session,
                SourceBuildJobFilters.ActiveForConfiguration(request.TenantId, request.ConfigurationId))
            .AnyAsync(ct);
        if (hasActiveJobs)
        {
            PersistDeletedSourceBuildConfigurationOutcome rejected
                = new SourceBuildConfigurationHasActiveJobs(request.ConfigurationId);
            return rejected;
        }

        var current = await configurations.Find(
                session,
                SourceBuildConfigurationFilters.ById(request.TenantId, request.ConfigurationId))
            .SingleAsync(ct);
        var target = await loadTarget(
            new SourceBuildTargetRequest(request.TenantId, current.WebServiceId),
            session,
            ct);
        if (target is SourceBuildTargetSnapshot bound && bound.ConfigurationId == request.ConfigurationId)
            return new SourceBuildConfigurationInUse(request.ConfigurationId, current.WebServiceId);

        var deleted = await configurations.FindOneAndDeleteAsync(
            session,
            SourceBuildConfigurationFilters.AtPlanningVersion(current),
            cancellationToken: ct);
        if (deleted is not null)
            await outbox(session, [OutboxWrite.Create(deleted.ToChangedEvent())], ct);
        PersistDeletedSourceBuildConfigurationOutcome outcome = deleted is not null
            ? new SourceBuildConfigurationRemovalRecorded()
            : new SourceBuildConfigurationNotFound(request.ConfigurationId);
        return outcome;
    }
}
