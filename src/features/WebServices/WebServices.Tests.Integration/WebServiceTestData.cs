// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.ASP.Helpers;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Testing;

namespace Pole.Features.WebServices.Tests.Integration;

internal static class WebServiceTestData
{
    internal static WebServiceSpecDto Spec()
    {
        var probe = new ProbeConfigDto("/health", "8080", 5, 10, 5, 3, 1);
        return new WebServiceSpecDto(
            new ImageInfoDto("docker.io", "nginx", "1.27"),
            new WebServiceConfigDto
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add(
                    "ASPNETCORE_ENVIRONMENT",
                    "Development"),
                PortMappings = [new PortMappingDto(80, "8080", "TCP", "http")],
            },
            null,
            new ProbeSpecDto(probe, probe, probe));
    }

    internal static CreateWebServiceRequest Create(string groupId, string name = "web-api") => new(
        new CreateWebServiceRequestBody(
            name,
            groupId,
            Spec(),
            new WebServicePlacementRequest(
                null,
                [new WebServiceDeploymentTargetRequest(TestPlatformDefaults.DeploymentTargetId, 2)]),
            []),
        TestPlatformDefaults.TenantId,
        new ClaimValue<string>(TestPlatformDefaults.UserId),
        new ClaimValue<string>(TestPlatformDefaults.UserEmail));
}
