namespace Pole.Features.DeploymentTargets.Contracts;

public static class DeploymentTargetVersionKeys
{
    public static string Revision(string targetId) => $"DeploymentTarget/{targetId}";

    public static string WorkloadConfiguration(string targetId) => $"{Revision(targetId)}/WorkloadConfiguration";
}
