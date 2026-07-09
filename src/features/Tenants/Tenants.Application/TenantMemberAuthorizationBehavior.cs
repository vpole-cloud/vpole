using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants;

public sealed class TenantMemberAuthorizationBehavior<TRequest, TResponse>(
    ITenantDbContext dbContext,
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
        if (request is not ITenantMemberOperation tenantOperation)
            return await next();

        var user = httpContextAccessor.HttpContext?.User;
        if (user is null || user.Identity?.IsAuthenticated != true)
            return Failures.Unauthorized("Authenticated user is required.", "Tenant member authorization");

        if (user.Claims.Any(c => c.Type == Claims.Roles && c.Value == Roles.Admin))
            return await next();

        var userId = user.FindFirstValue(Claims.UserId);
        if (string.IsNullOrWhiteSpace(userId))
            return Failures.Unauthorized("Authenticated user id is required.", "Tenant member authorization");

        var tenant = await dbContext.Tenants
            .Find(t => t.Id == tenantOperation.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
            return Failures.NotFound("Tenant not found.", tenantOperation.TenantId);

        var currentMember = tenant.Members.FirstOrDefault(m => m.UserId == userId);
        if (currentMember is null)
            return Failures.Forbidden("Tenant membership is required.", "Tenant member authorization");

        if (request is ITenantMemberReadOperation && !currentMember.Role.CanRead())
            return Failures.Forbidden("Tenant viewer role is required.", "Tenant member authorization");

        if (request is ITenantMemberManageOperation && !currentMember.Role.CanManageMembers())
            return Failures.Forbidden("Tenant admin role is required.", "Tenant member authorization");

        if (request is ITenantMemberRoleOperation roleOperation &&
            roleOperation.Role == TenantRole.Owner &&
            !currentMember.Role.CanManageTenant())
        {
            return Failures.Forbidden("Tenant owner role is required to grant owner role.", "Tenant member authorization");
        }

        var targetMemberId = request switch
        {
            ITenantMemberTargetOperation targetOperation => targetOperation.MemberId,
            ITenantMemberRoleOperation { MemberId: not null } roleTargetOperation => roleTargetOperation.MemberId,
            _ => null
        };

        if (targetMemberId is not null)
        {
            var targetMember = tenant.Members.FirstOrDefault(m => m.Id == targetMemberId);
            if (targetMember is null)
                return Failures.NotFound("Tenant member not found.", targetMemberId);

            if (targetMember.Role == TenantRole.Owner && !currentMember.Role.CanManageTenant())
                return Failures.Forbidden("Tenant owner role is required to manage owners.", "Tenant member authorization");
        }

        return await next();
    }
}

