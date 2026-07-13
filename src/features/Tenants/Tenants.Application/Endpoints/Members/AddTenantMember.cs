using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public sealed record AddTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromBody] AddTenantMemberRequestBody Body
) : IMediatorRequest<TenantMemberDto>, ITenantMemberRoleOperation
{
    public string? MemberId => null;
    public TenantRole Role => Body.Role;
}

public sealed record AddTenantMemberRequestBody(string UserId, string Email, TenantRole Role);

public sealed class AddTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<AddTenantMemberRequest, TenantMemberDto>
{
    public async Task<Result<TenantMemberDto>> HandleAsync(
        AddTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = TenantFactory.CreateMember(request.TenantId, request.Body.UserId, request.Body.Email, request.Body.Role);
        var updatedTenant = await dbContext.Tenants.FindOneAndUpdateAsync(
            t => t.Id == request.TenantId && t.Members.All(m => m.UserId != request.Body.UserId),
            Builders<Tenant>.Update.Push(t => t.Members, member),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        if (updatedTenant is not null)
            return member.ToDto();

        var tenant = await dbContext.Tenants
            .Find(t => t.Id == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
            return Failures.NotFound("Tenant not found.", request.TenantId);

        return tenant.Members.Any(m => m.UserId == request.Body.UserId)
            ? Failures.Conflict($"User '{request.Body.UserId}' is already a member of tenant '{request.TenantId}'.")
            : Failures.NotFound("Tenant not found.", request.TenantId);
    }
}

public sealed class AddTenantMemberValidator : AbstractValidator<AddTenantMemberRequest>
{
    public AddTenantMemberValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Body.UserId).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Body.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Body.Role).IsInEnum();
    }
}
