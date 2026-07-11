using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;

namespace Pole.Features.Tenants.Application.Middleware;

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

        if (AllowsAnonymousPoleCiToken(httpContext))
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
               path.StartsWithSegments("/api/v1/storage-profiles") ||
               path.StartsWithSegments("/api/v1/git-provider-connections") ||
               path.StartsWithSegments("/api/v1/source-repositories") ||
               path.StartsWithSegments("/api/v1/source-build-configurations") ||
               path.StartsWithSegments("/api/v1/source-build-jobs") ||
               path.StartsWithSegments("/api/v1/domain-names") ||
               path.StartsWithSegments("/api/v1/search");
    }

    private static bool AllowsAnonymousPoleCiToken(HttpContext httpContext)
    {
        var allowsAnonymous = httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        if (!allowsAnonymous)
            return false;

        var path = httpContext.Request.Path.Value;
        var isImageTagEndpoint = path?.StartsWith("/api/v1/web-services/", StringComparison.OrdinalIgnoreCase) == true &&
                                 path.EndsWith("/image-tag", StringComparison.OrdinalIgnoreCase);
        if (!isImageTagEndpoint)
            return false;

        var authorization = httpContext.Request.Headers.Authorization.FirstOrDefault();
        return authorization?.StartsWith("Bearer pole_ci_", StringComparison.OrdinalIgnoreCase) == true;
    }
}

