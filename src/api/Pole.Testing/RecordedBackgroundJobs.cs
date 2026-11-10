using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Pole.Testing;

/// <summary>Records test jobs without executing external work.</summary>
public sealed class RecordedBackgroundJobs : IBackgroundJobClient
{
    private readonly ConcurrentDictionary<string, (Job Job, IState State)> _jobs = new();
    public IReadOnlyDictionary<string, (Job Job, IState State)> Jobs => _jobs;

    public string Create(Job job, IState state)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = (job, state);
        return id;
    }

    public bool ChangeState(
        string jobId,
        IState state,
        string expectedState)
    {
        if (!_jobs.TryGetValue(jobId, out var current) ||
            (expectedState is not null && current.State.Name != expectedState))
            return false;
        return _jobs.TryUpdate(jobId, (current.Job, state), current);
    }
}
