using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application;

public sealed class TenantMemberAuthorizationBehavior<TRequest, TResponse>(
    LoadTenant loadTenant,
    IHttpContextAccessor httpContextAccessor
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMediatorRequest<TResponse>
    where TResponse : notnull
{
    public async Task<MediatorResult<TResponse>> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ITenantMemberOperation tenantOperation)
            return await next();

        var user = httpContextAccessor.HttpContext?.User;
        if (user is null || user.Identity?.IsAuthenticated != true)
            return Reject(new TenantAuthenticationRequired());

        if (user.Claims.Any(c => c.Type == Claims.Roles && c.Value == Roles.Admin))
            return await next();

        var userId = user.FindFirstValue(Claims.UserId);
        if (string.IsNullOrWhiteSpace(userId))
            return Reject(new TenantUserIdentityRequired());

        var tenant = await loadTenant(tenantOperation.TenantId, cancellationToken);

        if (tenant is null)
            return Reject(new TenantNotFound(tenantOperation.TenantId));

        var currentMember = tenant.Members.FirstOrDefault(m => m.UserId == userId);
        if (currentMember is null)
            return Reject(new TenantMembershipRequired(tenantOperation.TenantId));

        if (request is ITenantMemberReadOperation && !currentMember.Role.CanRead())
            return Reject(new TenantRoleRequired(TenantRole.Viewer));

        if (request is ITenantMemberManageOperation && !currentMember.Role.CanManageMembers())
            return Reject(new TenantRoleRequired(TenantRole.Admin));

        if (request is ITenantMemberRoleOperation roleOperation && roleOperation.Role == TenantRole.Owner &&
            !currentMember.Role.CanManageTenant())
            return Reject(new TenantRoleRequired(TenantRole.Owner));

        var targetMemberId = request switch
        {
            ITenantMemberTargetOperation targetOperation => targetOperation.MemberId,
            ITenantMemberRoleOperation { MemberId: not null } roleTargetOperation => roleTargetOperation.MemberId,
            _ => null,
        };

        if (targetMemberId is not null)
        {
            var targetMember = tenant.Members.FirstOrDefault(m => m.Id == targetMemberId);
            if (targetMember is null)
                return Reject(new TenantMemberNotFound(tenantOperation.TenantId, targetMemberId));

            if (targetMember.Role == TenantRole.Owner && !currentMember.Role.CanManageTenant())
                return Reject(new TenantRoleRequired(TenantRole.Owner));
        }

        return await next();
    }

    private static MediatorResult<TResponse> Reject(IMediatorRejection rejection) => new PipelineRejected(rejection);
}
