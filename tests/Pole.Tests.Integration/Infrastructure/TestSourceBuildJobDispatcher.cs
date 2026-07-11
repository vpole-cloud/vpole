using Pole.Features.SourceBuilds.Application.Jobs;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestSourceBuildJobDispatcher : ISourceBuildJobDispatcher
{
    public List<string> BuildJobIds { get; } = [];

    public string Enqueue(string buildJobId)
    {
        BuildJobIds.Add(buildJobId);
        return $"test-{buildJobId}";
    }
}
