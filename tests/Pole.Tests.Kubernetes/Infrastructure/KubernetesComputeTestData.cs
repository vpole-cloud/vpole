using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Tests.Kubernetes.Infrastructure;

internal static class KubernetesComputeTestData
{
    private const string ComputeProfileId = "507f1f77bcf86cd799439019";

    internal static ResolvedComputeConfiguration Compute() =>
        ResolvedComputeConfiguration.Create(
            ComputeProfileId, 1, "General purpose",
            ResolvedComputeResourceValues.Create(250, 256),
            ResolvedComputeResourceValues.Create(1000, 1024),
            ComputeArchitecture.Any);

    internal static ResourceDeploymentComputeConfiguration ContractCompute(string deploymentTargetId) =>
        new(ComputeProfileId, 1, deploymentTargetId, "General purpose",
            new(250, 256), new(1000, 1024), ComputeArchitecture.Any,
            [], [], [], [], [], RuntimeClassName: null);
}
