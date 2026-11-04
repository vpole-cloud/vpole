namespace Pole.Features.ResourceDeployments.Contracts;

public static class ResourceDeploymentVersionKeys
{
    public static string Revision(string id) => $"ResourceDeployment/{id}";

    public static string DesiredGeneration(string id) => $"{Revision(id)}/DesiredGeneration";

    public static string ResourceRevision(string id) => $"{Revision(id)}/ResourceRevision";

    public static string DeleteIntent(string id, string hash) => $"{Revision(id)}/DeleteIntent/{hash}";
}
