namespace Pole.Features.GitOps.Contracts;

// Version tokens and preview proofs compare these keys by ordinal string equality.
public static class DeclarationVersionKeys
{
    public static string DeploymentTarget(string targetId) => $"DeploymentTarget/{targetId}";

    public static string TargetWorkloadConfiguration(string targetId) =>
        $"{DeploymentTarget(targetId)}/WorkloadConfiguration";

    public static string ComputeProfilePolicy(string profileId) => $"ComputeProfile/{profileId}/Policy";

    public static string ComputeProfileRevision(string profileId, long revision) =>
        $"ComputeProfile/{profileId}/Revision/{revision}";

    public static string StorageProfilePolicy(string profileId) => $"StorageProfile/{profileId}/Policy";

    public static string PlanningFence(string kind, string resourceId) => $"{kind}/{resourceId}/PlanningFence";
}
