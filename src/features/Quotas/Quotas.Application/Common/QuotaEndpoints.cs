// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Quotas.Application.Endpoints;

namespace Pole.Features.Quotas.Application.Common;

public static class QuotaEndpoints
{
    public const string TenantRoute = "/tenant-quotas";
    public const string PlatformRoute = "/platform/tenants";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapQuotaEndpoints()
        {
            app.MapGroup(TenantRoute).WithTags("TenantQuotas").MapGetTenantQuotaUsageEndpoint();

            app.MapGroup(PlatformRoute)
                .WithoutTenantContext()
                .WithTags("PlatformQuotas")
                .MapGetTenantQuotaPolicyEndpoint()
                .MapUpsertTenantQuotaPolicyEndpoint();

            return app;
        }
    }
}
