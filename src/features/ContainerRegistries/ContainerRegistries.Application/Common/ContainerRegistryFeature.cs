// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ContainerRegistries.Application.Endpoints;

namespace Pole.Features.ContainerRegistries.Application.Common;

public static class ContainerRegistryFeature;

public static class ContainerRegistryEndpoints
{
    public static IEndpointRouteBuilder MapContainerRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/platform/encryption")
            .WithoutTenantContext()
            .WithTags("PlatformEncryption")
            .MapRewrapRegistryCredentialsEndpoint();
        return app;
    }
}
