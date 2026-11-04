namespace Pole.Features.GitOps.Contracts;

public static class DeclarationFieldPaths
{
    public const string DeploymentTarget = "spec.deploymentTarget";

    public const string Environment = "spec.environment";

    public const string Image = "spec.image";

    public const string MetadataName = "metadata.name";

    public const string Placement = "spec.placement";

    public const string PlacementComputeProfile = "spec.placement.computeProfile";

    public const string PlacementDeploymentTarget = "spec.placement.deploymentTarget";

    public const string PlacementIsolationScope = "spec.placement.isolationScope";

    public const string ResourceGroup = "spec.resourceGroup";

    public const string Spec = "spec";

    public const string StorageProfile = "spec.storage.profile";

    public const string StorageSizeGb = "spec.storage.sizeGb";

    public const string Tags = "spec.tags";

    public static string SecretEnvironment(string key) => $"spec.secretEnvironment.{key}";
}
