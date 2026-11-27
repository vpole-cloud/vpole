// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Routing;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class IsolationScopeEndpoints
{
    public static IEndpointRouteBuilder MapIsolationScopeEndpoints(this IEndpointRouteBuilder app) => app
        .MapCreateIsolationScopeEndpoint()
        .MapGetIsolationScopesEndpoint()
        .MapGetIsolationScopeEndpoint()
        .MapDeleteIsolationScopeEndpoint();
}
