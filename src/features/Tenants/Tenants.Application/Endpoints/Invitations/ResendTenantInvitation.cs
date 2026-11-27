// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record ResendTenantInvitationRequest(
    [FromRoute] string TenantId,
    [FromRoute] string InvitationId,
    [FromBody] InvitationRevisionBody Body,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<ResendTenantInvitationOutcome>, ITenantMemberManageOperation;

public union ResendTenantInvitationOutcome(
    InvitationDto,
    InvitationNotFound,
    InvitationRevisionConflict,
    InvitationForbidden,
    InvitationUnavailable,
    InvitationEncryptionUnavailable);

public sealed class ResendTenantInvitationValidator : AbstractValidator<ResendTenantInvitationRequest>
{
    public ResendTenantInvitationValidator()
    {
        RuleFor(request => request.TenantId).RouteObjectId();
        RuleFor(request => request.InvitationId).InvitationId();
        RuleFor(request => request.Body.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class ResendTenantInvitationHandler(
    ManageInvitation management
) : IMediatorOutcomeHandler<ResendTenantInvitationRequest, ResendTenantInvitationOutcome>
{
    public Task<ResendTenantInvitationOutcome> HandleAsync(
        ResendTenantInvitationRequest request,
        CancellationToken cancellationToken = default) => management.ResendAsync(
        new ManageInvitationRequest(
            request.TenantId,
            request.InvitationId,
            request.Body.ExpectedRevision,
            new InvitationActor(request.CurrentUserId)),
        cancellationToken);
}

public static class ResendTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapResendTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{TenantId}/invitations/{InvitationId}/resend",
                async (
                    [AsParameters] ResendTenantInvitationRequest request, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(request, ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationDto invitation => Results.Ok(invitation),
                        InvitationNotFound error => InvitationHttpProblems.NotFound(error),
                        InvitationRevisionConflict error => InvitationHttpProblems.RevisionConflict(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("ResendTenantInvitation");
        return app;
    }
}
