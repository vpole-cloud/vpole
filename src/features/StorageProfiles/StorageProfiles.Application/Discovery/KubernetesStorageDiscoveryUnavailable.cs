namespace Pole.Features.StorageProfiles.Application.Discovery;

public sealed record KubernetesStorageDiscoveryUnavailable(
    string DeploymentTargetId
);
