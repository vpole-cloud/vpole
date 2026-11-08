using System.Collections.Immutable;
using Pole.Features.WebServices.Application.Common;

namespace Pole.Tests.Kubernetes.WebServices;

internal static class WebServiceKubernetesTestData
{
    internal static WebServiceSpecDto Spec()
    {
        var probe = new ProbeConfigDto("/health", "8080", 5, 10, 5, 3, 1);
        return new(new ImageInfoDto("docker.io", "nginx", "1.27"),
            new WebServiceConfigDto
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT", "Development"),
                PortMappings = [new(80, "8080", "TCP", "http")]
            }, null, new ProbeSpecDto(probe, probe, probe));
    }
}
