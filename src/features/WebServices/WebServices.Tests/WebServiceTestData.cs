using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Workloads;

namespace Pole.Features.WebServices.Tests;

internal static class WebServiceTestData
{
    internal const string TenantId = "507f1f77bcf86cd799439011";

    internal static WebServiceSpecDto CreateSpec()
    {
        var probe = new ProbeConfigDto("/health", "8080", 5, 10, 5, 3, 1);
        return new WebServiceSpecDto(
            Image: new ImageInfoDto("docker.io", "nginx", "1.27"),
            Config: new WebServiceConfigDto
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT", "Development"),
                PortMappings = [new PortMappingDto(80, "8080", "TCP", "http")]
            },
            Autoscaling: null,
            Probes: new ProbeSpecDto(probe, probe, probe));
    }

    internal static ResourceDeploymentComputeConfiguration ComputeForTarget(string targetId) =>
        new(
            "507f1f77bcf86cd799439019", 1, targetId, "General purpose",
            new ResourceDeploymentComputeResourceValues(250, 256),
            new ResourceDeploymentComputeResourceValues(1000, 1024),
            ComputeArchitecture.Any, [], [], [], [], [], RuntimeClassName: null);
}
