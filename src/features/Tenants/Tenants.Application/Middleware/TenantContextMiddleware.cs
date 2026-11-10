using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Middleware;

public sealed class TenantContextMiddleware(
    RequestDelegate next
)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        LoadTenant loadTenant,
        IHttpProblemMapper httpProblems)
    {
        var tenantContextAccessor = httpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        tenantContextAccessor.Current = null;
        var endpoint = httpContext.GetEndpoint();
        var requirement = endpoint?.Metadata.GetMetadata<TenantEndpointMetadata>()?.ActiveTenant ??
            ActiveTenantRequirement.None;

        if (requirement == ActiveTenantRequirement.None || UsesEndpointCredential(httpContext))
        {
            await next(httpContext);
            return;
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await Reject(new TenantAuthenticationRequired());
            return;
        }

        var values = httpContext.Request.Headers[TenantHeaders.TenantId];
        if (values.Count == 0 && requirement == ActiveTenantRequirement.Optional)
        {
            await next(httpContext);
            return;
        }

        if (values.Count != 1 || !ObjectId.TryParse(values[0], out var parsedTenantId))
        {
            await Reject(
                new RequestValidationRejected(
                    new Dictionary<string, string[]>
                    {
                        [TenantHeaders.TenantId] = ["Exactly one valid tenant ObjectId is required."],
                    }));
            return;
        }

        var tenantId = parsedTenantId.ToString();
        var tenant = await loadTenant(tenantId, httpContext.RequestAborted);
        if (tenant is null)
        {
            await Reject(new TenantNotFound(tenantId));
            return;
        }

        var userId = httpContext.User.FindFirstValue(Claims.UserId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            await Reject(new TenantUserIdentityRequired());
            return;
        }

        var member = tenant.Members.FirstOrDefault(m => m.UserId == userId);
        var isPlatformAdmin
            = httpContext.User.Claims.Any(claim => claim.Type == Claims.Roles && claim.Value == Roles.Admin);
        if (member is null && !isPlatformAdmin)
        {
            await Reject(new TenantMembershipRequired(tenantId));
            return;
        }

        httpContext.Request.Headers[TenantHeaders.TenantId] = tenant.Id;
        tenantContextAccessor.Current = new TenantContext(
            tenant.Id,
            tenant.Name,
            member?.Role ?? TenantRole.Owner,
            isPlatformAdmin);
        await next(httpContext);
        return;

        Task Reject(IMediatorRejection rejection) =>
            httpProblems.Map(rejection).ToHttpResult().ExecuteAsync(httpContext);
    }

    private static bool UsesEndpointCredential(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();
        var credential = endpoint?.Metadata.GetMetadata<TenantCredentialMetadata>();
        if (credential is null || endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null)
            return false;

        var authorization = httpContext.Request.Headers.Authorization;
        return authorization.Count == 1 &&
            authorization[0]
                ?.StartsWith($"Bearer {credential.BearerTokenPrefix}", StringComparison.OrdinalIgnoreCase) == true;
    }
}
