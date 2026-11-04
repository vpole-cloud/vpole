using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitActiveRun(
    string RunId,
    long Generation);

public sealed record GitCommitProgress(
    string? Observed,
    string? Attempted,
    string? FullyApplied);

public sealed record GitSynchronizationState(
    [property: BsonId] string Id,
    GitSourceIdentity Source,
    long RequestedGeneration,
    long CompletedGeneration,
    long Fence,
    GitActiveRun? CurrentRun,
    GitCommitProgress Commits)
{
    public GitSynchronizationState RequestSync() => this with
    {
        RequestedGeneration = checked(RequestedGeneration + 1)
    };

    public GitSynchronizationState Observe(string commit)
    {
        GitSourceGuards.Required(commit, nameof(commit));
        return this with { Commits = Commits with { Observed = commit } };
    }

    public GitSynchronizationState BeginRun(string runId, string commit)
    {
        GitSourceGuards.Required(runId, nameof(runId));
        GitSourceGuards.Required(commit, nameof(commit));
        if (RequestedGeneration <= CompletedGeneration || CurrentRun is not null)
            throw new InvalidOperationException("A run needs pending work and no current run.");

        return this with
        {
            CurrentRun = new(runId, RequestedGeneration),
            Fence = checked(Fence + 1),
            Commits = Commits with { Attempted = commit }
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
            Commits = completion switch
            {
                GitRunCompletion.ConfigurationApplied => Commits with { FullyApplied = Commits.Attempted },
                GitRunCompletion.Blocked => Commits,
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            }
        };
    }
}

public enum GitRunCompletion { ConfigurationApplied, Blocked }

public static class GitSynchronizationStateFactory
{
    public static GitSynchronizationState Create(GitSourceIdentity source)
    {
        GitSourceGuards.Identity(source);
        return new($"{source.TenantId}/{source.SourceId}", source, 0, 0, 0, null, new(null, null, null));
    }
}
