using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitSchedulingFilters
{
    public static FilterDefinition<RepositorySource> EligibleSources(GitSchedulingEligibility eligibility) =>
        eligibility == GitSchedulingEligibility.Active
            ? Builders<RepositorySource>.Filter.Eq(item => item.Activity, GitSourceActivity.Active)
            : Builders<RepositorySource>.Filter.In(item => item.Activity,
                [GitSourceActivity.Active, GitSourceActivity.Paused, GitSourceActivity.Detaching]);

    public static FilterDefinition<RepositorySource> Repository(RepositorySyncSignal signal) =>
        Builders<RepositorySource>.Filter.Eq(item => item.Activity, GitSourceActivity.Active) &
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, signal.TenantId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.Provider, signal.Provider) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.ConnectionId, signal.ConnectionId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.ExternalRepositoryId,
            signal.ExternalRepositoryId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.Branch, signal.Branch);

    public static FilterDefinition<GitSynchronizationState> Pending() =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun, null) &
        Builders<GitSynchronizationState>.Filter.Where(item =>
            item.RequestedGeneration > item.CompletedGeneration);

    public static FilterDefinition<GitSynchronizationState> PollDue(DateTime now) =>
        Builders<GitSynchronizationState>.Filter.Lte(item => item.NextPollAtUtc, now);

    public static FilterDefinition<GitReconciliationRun> Recoverable() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunWaiting>(item => item.State) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunDelayed>(item => item.State) &
            Due("$State.NextAttemptAtUtc")) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunWaitingCleanup>(item => item.State) &
            Due("$State.NextAttemptAtUtc")) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunClaimed>(item => item.State) &
            Due("$State.Lease.ExpiresAtUtc"));

    // $$NOW must be evaluated by MongoDB, independently of the worker's clock.
    private static FilterDefinition<GitReconciliationRun> Due(string field) =>
        new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { field, "$$NOW" }));
}
