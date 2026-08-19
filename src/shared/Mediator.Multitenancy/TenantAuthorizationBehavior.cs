using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.ResultType;

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

        var failure = Authorize(request);
        if (failure is not null)
            return RejectLegacy(failure);

        return await next();
    }

    private Failure? Authorize(TRequest request)
    {
        if (request is ITenantOperation operation &&
            string.IsNullOrWhiteSpace(operation.TenantId))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
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
                return Forbidden("Platform admin role is required for tenant-wide operations.");

            return null;
        }

        if (!tenantContext.IsPlatformAdmin)
        {
            switch (request)
            {
                case ITenantWriteOperation when !tenantContext.Role.CanWrite():
                    return Forbidden("Tenant member role is required.");
                case ITenantReadOperation when !tenantContext.Role.CanRead():
                    return Forbidden("Tenant viewer role is required.");
            }
        }

        return null;
    }

    private static Failure Forbidden(string message) => Failures.Forbidden(message, "Tenant authorization");

    private static MediatorResult<TResponse> RejectLegacy(Failure failure) =>
        new PipelineRejected(new LegacyResultRejected(failure));
}

public static class ClaimPrincipalExtensions
{
    extension(ClaimsPrincipal claimsPrincipal)
    {
        public IEnumerable<string> Roles => claimsPrincipal.Claims.Where(c => c.Type == Claims.Roles).Select(c => c.Value);
    }
}
