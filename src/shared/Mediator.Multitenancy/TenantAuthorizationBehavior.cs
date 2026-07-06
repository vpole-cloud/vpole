using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.ResultType;

namespace Pole.Mediator;

public sealed class TenantAuthorizationBehavior<TRequest, TResponse>(
    ITenantContextAccessor tenantContextAccessor,
    ITenantOperationScopeAccessor operationScopeAccessor,
    IHttpContextAccessor httpContextAccessor)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<Result<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var isRead = request is ITenantReadOperation;
        var isWrite = request is ITenantWriteOperation;

        if (!isRead && !isWrite)
            return await next();

        var previousScope = operationScopeAccessor.Current;
        try
        {
            var authorization = BuildScope(request);
            if (authorization.Failure is not null)
                return authorization.Failure;

            operationScopeAccessor.Current = authorization.Scope;
            return await next();
        }
        finally
        {
            operationScopeAccessor.Current = previousScope;
        }
    }

    private TenantAuthorizationResult BuildScope(TRequest request)
    {
        var tenantContext = tenantContextAccessor.Current;
        var httpContext = httpContextAccessor.HttpContext;
        var isPlatformAdmin = tenantContext?.IsPlatformAdmin == true || httpContext?.User.Roles.Contains(Roles.Admin) is true;

        if (tenantContext is null)
        {
            if (!isPlatformAdmin)
                return TenantAuthorizationResult.Forbidden("Platform admin role is required for tenant-wide operations.");

            if (request is ITenantCreateOperation)
                return TenantAuthorizationResult.Forbidden($"Tenant-scoped create operations require {TenantHeaders.TenantId}.");

            return TenantAuthorizationResult.Success(new TenantOperationScope(null));
        }

        if (!tenantContext.IsPlatformAdmin)
        {
            switch (request)
            {
                case ITenantWriteOperation when !tenantContext.Role.CanWrite():
                    return TenantAuthorizationResult.Forbidden("Tenant member role is required.");
                case ITenantReadOperation when !tenantContext.Role.CanRead():
                    return TenantAuthorizationResult.Forbidden("Tenant viewer role is required.");
            }
        }

        return TenantAuthorizationResult.Success(new TenantOperationScope(tenantContext.TenantId));
    }

    private sealed record TenantAuthorizationResult(TenantOperationScope? Scope, Failure? Failure)
    {
        public static TenantAuthorizationResult Success(TenantOperationScope scope) => new(scope, null);
        public static TenantAuthorizationResult Forbidden(string message) => new(null, Result.Forbidden(message, "Tenant authorization"));
    }
}

public static class ClaimPrincipalExtensions
{
    extension(ClaimsPrincipal claimsPrincipal)
    {
        public IEnumerable<string> Roles => claimsPrincipal.Claims.Where(c => c.Type == Claims.Roles).Select(c => c.Value);
    }
}
