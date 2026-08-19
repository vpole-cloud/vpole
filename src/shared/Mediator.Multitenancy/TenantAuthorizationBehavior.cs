using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;

namespace Pole.Mediator;

public sealed class TenantAuthorizationBehavior<TRequest, TResponse>(
    ITenantContextAccessor tenantContextAccessor,
    IHttpContextAccessor httpContextAccessor)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var isRead = request is ITenantReadOperation;
        var isWrite = request is ITenantWriteOperation;

        if (!isRead && !isWrite)
            return await next();

        var rejection = Authorize(request);
        if (rejection is not null)
            return new PipelineRejected(rejection);

        return await next();
    }

    private IMediatorRejection? Authorize(TRequest request)
    {
        if (request is ITenantOperation operation &&
            string.IsNullOrWhiteSpace(operation.TenantId))
        {
            return new RequestValidationRejected(new Dictionary<string, string[]>
            {
                [TenantHeaders.TenantId] =
                [
                    $"{TenantHeaders.TenantId} is required for tenant-scoped operations."
                ]
            });
        }

        var tenantContext = tenantContextAccessor.Current;
        var httpContext = httpContextAccessor.HttpContext;
        var isPlatformAdmin = tenantContext?.IsPlatformAdmin == true || httpContext?.User.Roles.Contains(Roles.Admin) is true;

        if (tenantContext is null)
        {
            if (!isPlatformAdmin)
                return new TenantPlatformAdminRequired();

            return null;
        }

        if (!tenantContext.IsPlatformAdmin)
        {
            switch (request)
            {
                case ITenantWriteOperation when !tenantContext.Role.CanWrite():
                    return new TenantRoleRequired(TenantRole.Member);
                case ITenantReadOperation when !tenantContext.Role.CanRead():
                    return new TenantRoleRequired(TenantRole.Viewer);
            }
        }

        return null;
    }
}

public static class ClaimPrincipalExtensions
{
    extension(ClaimsPrincipal claimsPrincipal)
    {
        public IEnumerable<string> Roles => claimsPrincipal.Claims.Where(c => c.Type == Claims.Roles).Select(c => c.Value);
    }
}
