using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Core.Multitenancy;
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
            app.MapGroup(TenantRoute)
                .WithMetadata(new TenantEndpointMetadata())
                .WithTags("TenantQuotas")
                .MapGetTenantQuotaUsageEndpoint();

            app.MapGroup(PlatformRoute)
            .WithMetadata(new TenantEndpointMetadata(ActiveTenantRequirement.None))
                .WithTags("PlatformQuotas")
                .MapGetTenantQuotaPolicyEndpoint()
                .MapUpsertTenantQuotaPolicyEndpoint();

            return app;
        }
    }
}
