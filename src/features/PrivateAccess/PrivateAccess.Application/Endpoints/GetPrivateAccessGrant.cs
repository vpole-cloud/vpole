using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Persistence;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record GetPrivateAccessGrantRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue ActorUserId)
    : IMediatorRequest<GetPrivateAccessGrantOutcome>, ITenantReadOperation;

public union GetPrivateAccessGrantOutcome(
    PrivateAccessGrantDto,
    PrivateAccessGrantMissing,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessSessionSubjectMismatch);

public sealed class GetPrivateAccessGrantHandler(
    LoadTenantMember loadTenantMember,
    LoadPrivateAccessGrant loadGrant)
    : IMediatorOutcomeHandler<GetPrivateAccessGrantRequest, GetPrivateAccessGrantOutcome>
{
    public async Task<GetPrivateAccessGrantOutcome> HandleAsync(
        GetPrivateAccessGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = request.ActorUserId.Value;
        var member = await loadTenantMember(
            new LoadTenantMemberRequest(
                TenantId: request.TenantId,
                UserId: actor),
            cancellationToken: cancellationToken);
        if (member is not TenantMemberSnapshot membership)
            return new PrivateAccessSubjectMembershipRequired();
        var grant = await loadGrant(request.TenantId, request.Id, cancellationToken);
        if (grant is null)
            return new PrivateAccessGrantMissing();
        if (grant.SubjectUserId != actor && membership.Role < TenantMembershipRole.Admin)
            return new PrivateAccessSessionSubjectMismatch();
        return grant.ToDto();
    }
}

public sealed class GetPrivateAccessGrantValidator : AbstractValidator<GetPrivateAccessGrantRequest>
{
    public GetPrivateAccessGrantValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Grant ID");
    }
}

public static class GetPrivateAccessGrantEndpoint
{
    public static IEndpointRouteBuilder MapGetPrivateAccessGrantEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetPrivateAccessGrantRequest request,
                CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                PrivateAccessGrantDto grant => Results.Ok(grant),
                PrivateAccessGrantMissing error => PrivateAccessHttpProblemMappings.Map(error),
                PrivateAccessSubjectMembershipRequired error => PrivateAccessHttpProblemMappings.Map(error),
                PrivateAccessSessionSubjectMismatch error => PrivateAccessHttpProblemMappings.Map(error)
            }))
            .WithName("GetPrivateAccessGrant")
            .Produces<PrivateAccessGrantDto>(StatusCodes.Status200OK);
        return app;
    }
}
