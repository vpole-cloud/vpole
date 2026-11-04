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

    public static string PlanningFence(string kind, string resourceId) => $"{Resource(kind, resourceId)}/PlanningFence";

    public static string Resource(string kind, string resourceId) => $"{kind}/{resourceId}";

    public static string DeploymentDeleteIntent(string id, string hash) =>
        $"{ResourceDeployment(id)}/DeleteIntent/{hash}";

    public static string DeploymentDesiredGeneration(string id) => $"{ResourceDeployment(id)}/DesiredGeneration";

    public static string DeploymentResourceRevision(string id) => $"{ResourceDeployment(id)}/ResourceRevision";

    public static string DomainZone(string id) => $"DomainZone/{id}";

    public static string DomainZoneFence(string id) => $"DomainZoneFence/{id}";

    public static string DomainZoneReferenceFence(string id) => $"{DomainZone(id)}/ReferenceFence";

    public static string GitProviderConnection(string id) => $"GitProviderConnection/{id}";

    public static string HostnameBinding(string id) => $"HostnameBinding/{id}";

    public static string IsolationScope(string id) => $"IsolationScope/{id}";

    public static string ResourceConnection(string id) => $"ResourceConnection/{id}";

    public static string ResourceConnectionReferenceFence(string id) => $"{ResourceConnection(id)}/ReferenceFence";

    public static string ResourceDeployment(string id) => $"ResourceDeployment/{id}";

    public static string ResourceGroup(string id) => $"ResourceGroup/{id}";

    public static string ResourceGroupFence(string id) => $"ResourceGroupFence/{id}";

    public static string ResourceRegistration(string id) => $"ResourceRegistration/{id}";

    public static string Secret(string id) => $"Secret/{id}";

    public static string SecretReferenceFence(string id) => $"{Secret(id)}/ReferenceFence";

    public static string SecretValueVersion(string id) => $"{Secret(id)}/ValueVersion";

    public static string SourceBuildConfiguration(string id) => $"SourceBuildConfiguration/{id}";

    public static string SourceBuildConfigurationFence(string id) => $"SourceBuildConfigurationFence/{id}";

    public static string SourceBuildGeneration(string id) => $"SourceBuildGeneration/{id}";

    public static string SourceRepository(string id) => $"SourceRepository/{id}";

    public static string SourceRepositoryFence(string id) => $"SourceRepositoryFence/{id}";

    public static string WebService(string id) => $"WebService/{id}";
}
