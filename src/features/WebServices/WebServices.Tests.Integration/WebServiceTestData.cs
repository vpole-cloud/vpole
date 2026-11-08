using System.Collections.Immutable;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Testing;

namespace Pole.Features.WebServices.Tests.Integration;

internal static class WebServiceTestData
{
    internal static WebServiceSpecDto Spec()
    {
        var probe = new ProbeConfigDto("/health", "8080", 5, 10, 5, 3, 1);
        return new(new ImageInfoDto("docker.io", "nginx", "1.27"),
            new WebServiceConfigDto
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT", "Development"),
                PortMappings = [new(80, "8080", "TCP", "http")]
            }, null, new(probe, probe, probe));
    }

    internal static CreateWebServiceRequest Create(string groupId, string name = "web-api") =>
        new(new(name, groupId, Spec(), new(null, [new(TestPlatformDefaults.DeploymentTargetId, 2)]), []),
            TestPlatformDefaults.TenantId, new(TestPlatformDefaults.UserId), new(TestPlatformDefaults.UserEmail));
}
