using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public static class TenantMembers
{
    public static IEndpointRouteBuilder MapTenantMemberEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{TenantId}/members", async (IMediator mediator, GetTenantMembersRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(Results.Ok);
        })
        .WithName("GetTenantMembers")
        .Produces<IReadOnlyCollection<TenantMemberDto>>();

        app.MapPost("/{TenantId}/members", async (IMediator mediator, AddTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(m => Results.Created($"/api/v1/tenants/{m.TenantId}/members/{m.Id}", m));
        })
        .WithName("AddTenantMember")
        .Produces<TenantMemberDto>(StatusCodes.Status201Created);

        app.MapPut("/{TenantId}/members/{MemberId}", async (IMediator mediator, UpdateTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(Results.Ok);
        })
        .WithName("UpdateTenantMember")
        .Produces<TenantMemberDto>();

        app.MapDelete("/{TenantId}/members/{MemberId}", async (IMediator mediator, DeleteTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        })
        .WithName("DeleteTenantMember")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record GetTenantMembersRequest(
    [FromRoute] string TenantId,
    [FromClaim(Claims.UserId)] string CurrentUserId,
    [FromClaim(Claims.Roles)] ImmutableArray<string> Roles
) : IMediatorRequest<IReadOnlyCollection<TenantMemberDto>>, ITenantMemberReadOperation;

public sealed record AddTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromBody] AddTenantMemberRequestBody Body,
    [FromClaim(Claims.UserId)] string CurrentUserId,
    [FromClaim(Claims.Roles)] ImmutableArray<string> Roles
) : IMediatorRequest<TenantMemberDto>, ITenantMemberRoleOperation
{
    public string? MemberId => null;
    public TenantRole Role => Body.Role;
}

public sealed record AddTenantMemberRequestBody(string UserId, string Email, TenantRole Role);

public sealed record UpdateTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId,
    [FromBody] UpdateTenantMemberRequestBody Body,
    [FromClaim(Claims.UserId)] string CurrentUserId,
    [FromClaim(Claims.Roles)] ImmutableArray<string> Roles
) : IMediatorRequest<TenantMemberDto>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public sealed record UpdateTenantMemberRequestBody(TenantRole Role);

public sealed record DeleteTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId,
    [FromClaim(Claims.UserId)] string CurrentUserId,
    [FromClaim(Claims.Roles)] ImmutableArray<string> Roles
) : IMediatorRequest<string>, ITenantMemberTargetOperation;

public sealed class GetTenantMembersHandler(ITenantDbContext dbContext)
    : IMediatorHandler<GetTenantMembersRequest, IReadOnlyCollection<TenantMemberDto>>
{
    public async Task<Result<IReadOnlyCollection<TenantMemberDto>>> HandleAsync(GetTenantMembersRequest request, CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants
            .Find(t => t.Id == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return tenant is null
            ? Result.NotFound($"Tenant '{request.TenantId}' was not found.")
            : tenant.Members.Select(m => m.ToDto()).ToList();
    }
}

public sealed class AddTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<AddTenantMemberRequest, TenantMemberDto>
{
    public async Task<Result<TenantMemberDto>> HandleAsync(AddTenantMemberRequest request, CancellationToken cancellationToken = default)
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
            return Result.NotFound($"Tenant '{request.TenantId}' was not found.");

        return tenant.Members.Any(m => m.UserId == request.Body.UserId)
            ? Result.Conflict($"User '{request.Body.UserId}' is already a member of tenant '{request.TenantId}'.")
            : Result.NotFound($"Tenant '{request.TenantId}' was not found.");
    }
}

public sealed class UpdateTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<UpdateTenantMemberRequest, TenantMemberDto>
{
    public async Task<Result<TenantMemberDto>> HandleAsync(UpdateTenantMemberRequest request, CancellationToken cancellationToken = default)
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
            ? Result.NotFound($"Tenant member '{request.MemberId}' was not found.")
            : member.ToDto();
    }
}

public sealed class DeleteTenantMemberHandler(ITenantDbContext dbContext)
    : IMediatorHandler<DeleteTenantMemberRequest, string>
{
    public async Task<Result<string>> HandleAsync(DeleteTenantMemberRequest request, CancellationToken cancellationToken = default)
    {
        var ownerValidation = await TenantMemberMutationGuard.ValidateAsync(
            dbContext,
            request.TenantId,
            request.MemberId,
            nextRole: null,
            cancellationToken);
        if (ownerValidation is not null)
            return ownerValidation;

        var result = await dbContext.Tenants.UpdateOneAsync(
            t => t.Id == request.TenantId && t.Members.Any(m => m.Id == request.MemberId),
            Builders<Tenant>.Update.PullFilter(t => t.Members, m => m.Id == request.MemberId),
            cancellationToken: cancellationToken);

        return result.ModifiedCount == 0
            ? Result.NotFound($"Tenant member '{request.MemberId}' was not found.")
            : request.MemberId;
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

internal static class TenantMemberMutationGuard
{
    public static async Task<Failure?> ValidateAsync(
        ITenantDbContext dbContext,
        string tenantId,
        string memberId,
        TenantRole? nextRole,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .Find(t => t.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
            return Result.NotFound($"Tenant '{tenantId}' was not found.");

        var existingMember = tenant.Members.FirstOrDefault(m => m.Id == memberId);
        if (existingMember is null)
            return Result.NotFound($"Tenant member '{memberId}' was not found.");

        var removesOwner = nextRole is null || nextRole != TenantRole.Owner;
        if (existingMember.Role == TenantRole.Owner &&
            removesOwner &&
            tenant.Members.Count(m => m.Role == TenantRole.Owner) == 1)
        {
            return Result.Conflict("Tenant must keep at least one owner.");
        }

        return null;
    }
}

