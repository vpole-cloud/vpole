using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitRepositoryFilters
{
    public static FilterDefinition<RepositorySource> Tenant(string tenant) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant);

    public static FilterDefinition<RepositorySource> VisibleTenant(string tenant) =>
        Tenant(tenant) & Builders<RepositorySource>.Filter.Ne(item => item.Activity, GitSourceActivity.Deleted);

    public static FilterDefinition<RepositorySource> VisibleSource(string tenant, string source) =>
        Source(tenant, source) & Builders<RepositorySource>.Filter.Ne(item => item.Activity, GitSourceActivity.Deleted);

    public static FilterDefinition<GitSynchronizationState> States(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Source.TenantId, tenant) &
        Builders<GitSynchronizationState>.Filter.In(item => item.Source.SourceId, ids);

    public static FilterDefinition<GitReconciliationRun> Runs(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.In(item => item.Id, ids);

    public static FilterDefinition<GitReconciliationRun> SourceRuns(string tenant, string source) =>
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.SourceId, source);

    public static FilterDefinition<ManagedDeclaration> Inventory(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.In(item => item.SourceId, ids);

    public static FilterDefinition<GitOperationReceipt> Receipts(string tenant, string source, string run) =>
        Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.SourceId, source) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.RunId, run);

    public static FilterDefinition<GitSourceRequestReceipt> Request(string key) =>
        Builders<GitSourceRequestReceipt>.Filter.Eq(item => item.Id, key);

    public static string StateId(string tenant, string source) => $"{tenant}/{source}";

    public static FilterDefinition<RepositorySource> Source(string tenant, string source) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<RepositorySource>.Filter.Eq(item => item.Id, source);

    public static FilterDefinition<RepositorySource> Creation(string tenant, string key) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<RepositorySource>.Filter.Eq(item => item.CreationKey, key);

    public static FilterDefinition<GitSynchronizationState> State(string tenant, string source) =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Id, StateId(tenant, source));

    public static FilterDefinition<GitReconciliationRun> Run(string tenant, string source, string run) =>
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.SourceId, source) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Id, run);

    public static FilterDefinition<GitReconciliationRun> PreviousRuns(GitRunStamp stamp) =>
        SourceRuns(stamp.Source.TenantId, stamp.Source.SourceId) &
        Builders<GitReconciliationRun>.Filter.Lt(item => item.Stamp.Generation, stamp.Generation);

    public static FilterDefinition<GitReconciliationRun> LifecycleInput() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunInput, GitInputLifecycle>(item => item.Input);

    public static FilterDefinition<GitReconciliationRun> HumanConfirmedLifecycle() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunInput, GitInputLifecycle>(item => item.Input) &
        Builders<GitReconciliationRun>.Filter.OfType<GitPreviewState, GitPreviewConfirmed>(
            item => ((GitInputLifecycle)item.Input).Plan.Authorization);

    public static FilterDefinition<GitReconciliationRun> PriorHumanLifecycle(GitRunStamp stamp) =>
        PreviousRuns(stamp) & HumanConfirmedLifecycle();

    public static FilterDefinition<RepositorySource> SourceRevision(GitRunStamp stamp) =>
        Source(stamp.Source.TenantId, stamp.Source.SourceId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Revision, stamp.ConfigurationRevision);

    public static FilterDefinition<GitSynchronizationState> ActiveState(GitRunStamp stamp) =>
        State(stamp.Source.TenantId, stamp.Source.SourceId) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Fence, stamp.Fence) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun!.RunId, stamp.RunId) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun!.Generation, stamp.Generation);

    public static FilterDefinition<GitReconciliationRun> LiveClaim(
        GitRunStamp stamp,
        GitClaimIdentity claim) => Run(stamp.Source.TenantId, stamp.Source.SourceId, stamp.RunId) &
                                   Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Fence, stamp.Fence) &
                                   Builders<GitReconciliationRun>.Filter.Eq(
                                       item => item.Stamp.ConfigurationRevision,
                                       stamp.ConfigurationRevision) &
                                   Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunClaimed>(item =>
                                       item.State) &
                                   Builders<GitReconciliationRun>.Filter.Eq(
                                       item => ((GitRunClaimed)item.State).Lease.WorkerId, claim.WorkerId) &
                                   Builders<GitReconciliationRun>.Filter.Eq(
                                       item => ((GitRunClaimed)item.State).Lease.Attempt, claim.Attempt) &
                                   Builders<GitReconciliationRun>.Filter.AfterServerNow(
                                       item => ((GitRunClaimed)item.State).Lease.ExpiresAtUtc);
}

internal sealed class GitDatabaseClock(IMongoCollection<RepositorySource> sources)
{
    public async Task<DateTime> NowAsync(GitSourceIdentity identity, IClientSessionHandle session, CancellationToken ct)
    {
        var document = await sources.Aggregate(session)
            .Match(GitRepositoryFilters.Source(identity.TenantId, identity.SourceId))
            .Project(new BsonDocument("Now", "$$NOW")).FirstAsync(ct);
        return document["Now"].AsBsonDateTime.ToUniversalTime();
    }
}
