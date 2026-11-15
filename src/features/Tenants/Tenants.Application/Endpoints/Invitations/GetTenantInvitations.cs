using FluentValidation;
using MongoDB.Bson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record GetTenantInvitationsRequest(
    [FromRoute] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<GetTenantInvitationsOutcome>, ITenantMemberManageOperation;

public union GetTenantInvitationsOutcome(
    IReadOnlyList<InvitationDto>,
    TenantNotFound,
    InvitationForbidden);

public sealed class GetTenantInvitationsValidator : AbstractValidator<GetTenantInvitationsRequest>
{
    public GetTenantInvitationsValidator() =>
        RuleFor(request => request.TenantId).Must(value => ObjectId.TryParse(value, out _));
}

public sealed class GetTenantInvitationsHandler(
    MongoLoadInvitation load
) : IMediatorOutcomeHandler<GetTenantInvitationsRequest, GetTenantInvitationsOutcome>
{
    public async Task<GetTenantInvitationsOutcome> HandleAsync(
        GetTenantInvitationsRequest request,
        CancellationToken cancellationToken = default) => await load.ListAsync(
        request.TenantId,
        new InvitationActor(request.CurrentUserId),
        cancellationToken);
}

public static class GetTenantInvitationsEndpoint
{
    public static IEndpointRouteBuilder MapGetTenantInvitationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{TenantId}/invitations",
                async (
                    [AsParameters] GetTenantInvitationsRequest request, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(request, ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<InvitationDto> invitations => Results.Ok(invitations),
                        TenantNotFound error => TenantMemberAuthorizationHttpProblemMappings.MapTenantNotFound(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                    });
                })
            .WithName("GetTenantInvitations");
        return app;
    }
}
