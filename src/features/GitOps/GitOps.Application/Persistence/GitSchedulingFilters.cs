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
        new BsonDocument("$expr",
            new BsonDocument("$gt",
                new BsonArray
                    { "$RequestedGeneration", "$CompletedGeneration" }));

    public static FilterDefinition<GitSynchronizationState> PollDue(DateTime now) =>
        Builders<GitSynchronizationState>.Filter.Lte(item => item.NextPollAtUtc, now);

    public static FilterDefinition<GitReconciliationRun> Recoverable() =>
        new BsonDocument("$or",
            new BsonArray
            {
                new BsonDocument("State._t", nameof(GitRunWaiting)),
                new BsonDocument
                {
                    { "State._t", nameof(GitRunDelayed) },
                    { "$expr", new BsonDocument("$lte", new BsonArray { "$State.NextAttemptAtUtc", "$$NOW" }) }
                },
                new BsonDocument
                {
                    { "State._t", nameof(GitRunWaitingCleanup) },
                    { "$expr", new BsonDocument("$lte", new BsonArray { "$State.NextAttemptAtUtc", "$$NOW" }) }
                },
                new BsonDocument
                {
                    { "State._t", nameof(GitRunClaimed) },
                    { "$expr", new BsonDocument("$lte", new BsonArray { "$State.Lease.ExpiresAtUtc", "$$NOW" }) }
                }
            });
}
