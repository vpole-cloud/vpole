namespace Pole.Features.SourceBuilds.Contracts;

public static class SourceBuildConfigurationVersionKeys
{
    public static string Revision(string id) => $"SourceBuildConfiguration/{id}";
}
