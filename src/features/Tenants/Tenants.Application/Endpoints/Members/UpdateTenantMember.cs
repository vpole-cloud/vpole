using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public sealed record UpdateTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId,
    [FromBody] UpdateTenantMemberRequestBody Body
) : IMediatorRequest<TenantMemberDto>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public sealed record UpdateTenantMemberRequestBody(TenantRole Role);

public sealed class UpdateTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<UpdateTenantMemberRequest, TenantMemberDto>
{
    public async Task<Result<TenantMemberDto>> HandleAsync(
        UpdateTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerValidation = await TenantMemberMutationGuard.ValidateAsync(
            dbContext,
            request.TenantId,
            request.MemberId,
            nextRole: request.Body.Role,
            cancellationToken);
        if (ownerValidation is not null)
            return ownerValidation;

        var updated = await dbContext.Tenants.FindOneAndUpdateAsync(
            t => t.Id == request.TenantId && t.Members.Any(m => m.Id == request.MemberId),
            Builders<Tenant>.Update.Set("Members.$.Role", request.Body.Role),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        var member = updated?.Members.FirstOrDefault(m => m.Id == request.MemberId);
        return member is null
            ? Failures.NotFound("Tenant member not found.", request.MemberId)
            : member.ToDto();
    }
}
