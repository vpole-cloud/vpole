using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Common;

namespace Pole.Features.Tenants.Middleware;

public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, ITenantDbContext dbContext)
    {
        var tenantContextAccessor = httpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        tenantContextAccessor.Current = null;

        if (!RequiresTenant(httpContext))
        {
            await next(httpContext);
            return;
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var tenantId = httpContext.Request.Headers[TenantHeaders.TenantId].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            await next(httpContext);
            return;
        }

        var tenant = await dbContext.Tenants
            .Find(t => t.Id == tenantId)
            .FirstOrDefaultAsync(httpContext.RequestAborted);

        if (tenant is null)
        {
            await Results.NotFound(new { error = $"Tenant '{tenantId}' was not found." }).ExecuteAsync(httpContext);
            return;
        }

        var userId = httpContext.User.FindFirstValue(Claims.UserId);
        var member = userId is null
            ? null
            : tenant.Members.FirstOrDefault(m => m.UserId == userId);

        var isPlatformAdmin = httpContext.User.IsInRole(Roles.Admin);
        if (member is null && !isPlatformAdmin)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        tenantContextAccessor.Current = new TenantContext(
            tenant.Id,
            tenant.Name,
            member?.Role ?? TenantRole.Owner,
            isPlatformAdmin);

        await next(httpContext);
    }

    private static bool RequiresTenant(HttpContext httpContext)
    {
        var path = httpContext.Request.Path;
        return path.StartsWithSegments("/api/v1/resource-groups") ||
               path.StartsWithSegments("/api/v1/web-services") ||
               path.StartsWithSegments("/api/v1/redis-services") ||
               path.StartsWithSegments("/api/v1/postgres") ||
               path.StartsWithSegments("/api/v1/domain-names") ||
               path.StartsWithSegments("/api/v1/search");
    }
}
