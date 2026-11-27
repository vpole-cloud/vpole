// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Routing;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class RuntimeCellEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeCellEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGetRuntimeCellsEndpoint().MapGetRuntimeCellEndpoint().MapRetryRuntimeCellEndpoint();
}
