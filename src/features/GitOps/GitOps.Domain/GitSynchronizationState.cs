using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitActiveRun(
    string RunId,
    long Generation);

public sealed record GitCommitProgress(
    string? Observed,
    string? Attempted,
    string? FullyApplied);

public sealed record GitPendingRequest(
    GitRunInitiator Initiator,
    string? RetryOfRunId);

public sealed record GitSynchronizationState(
    [property: BsonId] string Id,
    GitSourceIdentity Source,
    long RequestedGeneration,
    long CompletedGeneration,
    long Fence,
    GitActiveRun? CurrentRun,
    string? LatestRunId,
    GitPendingRequest Pending,
    GitCommitProgress Commits,
    DateTime NextPollAtUtc,
    long MutationVersion)
{
    public GitSynchronizationState RequestSync(GitPendingRequest request) => this with
    {
        RequestedGeneration = checked(RequestedGeneration + 1),
        Pending = request
    };

    public GitSynchronizationState Observe(string commit)
    {
        GitSourceGuards.Required(commit, nameof(commit));
        return this with { Commits = Commits with { Observed = commit } };
    }

    public GitSynchronizationState Attempt(string commit)
    {
        GitSourceGuards.Required(commit, nameof(commit));
        return this with { Commits = Commits with { Attempted = commit } };
    }

    public GitSynchronizationState BeginRun(string runId)
    {
        GitSourceGuards.Required(runId, nameof(runId));
        if (RequestedGeneration <= CompletedGeneration || CurrentRun is not null)
            throw new InvalidOperationException("A run needs pending work and no current run.");

        return this with
        {
            CurrentRun = new(runId, RequestedGeneration),
            LatestRunId = runId,
            Fence = checked(Fence + 1)
        };
    }

    public GitSynchronizationState CompleteRun(GitRunStamp stamp, GitRunCompletion completion)
    {
        if (stamp.Source != Source || stamp.RunId != CurrentRun?.RunId || stamp.Fence != Fence ||
            stamp.Generation != CurrentRun.Generation)
            throw new InvalidOperationException("Only the current fenced run can complete its requested generation.");

        return this with
        {
            CurrentRun = null,
            CompletedGeneration = stamp.Generation,
            Commits = completion == GitRunCompletion.ConfigurationApplied
                ? Commits with { FullyApplied = Commits.Attempted }
                : Commits
        };
    }
}

public static class GitSynchronizationStateFactory
{
    public static GitSynchronizationState Create(GitSourceIdentity source, DateTime nextPollAtUtc)
    {
        GitSourceGuards.Identity(source);
        GitSourceGuards.Utc(nextPollAtUtc);
        return new($"{source.TenantId}/{source.SourceId}",
            source,
            0,
            0,
            0,
            null,
            null,
            new(new(GitRunTrigger.Poll, null), null),
            new(null, null, null),
            nextPollAtUtc,
            0);
    }
}
