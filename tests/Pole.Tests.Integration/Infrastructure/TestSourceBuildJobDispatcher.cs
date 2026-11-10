namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestSourceBuildJobDispatcher
{
    public List<string> BuildJobIds { get; } = [];

    public string Invoke(string buildJobId)
    {
        BuildJobIds.Add(buildJobId);
        return $"test-{buildJobId}";
    }
}
