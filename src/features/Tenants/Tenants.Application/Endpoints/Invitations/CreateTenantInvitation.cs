// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;
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
using static Pole.Features.Tenants.Application.TenantMemberAuthorizationHttpProblemMappings;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record CreateTenantInvitationBody(
    string Email,
    [property: JsonRequired] TenantRole Role
);

public sealed record CreateTenantInvitationRequest(
    [FromRoute] string TenantId,
    [FromBody] CreateTenantInvitationBody Body,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<CreateTenantInvitationOutcome>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public union CreateTenantInvitationOutcome(
    InvitationDto,
    TenantNotFound,
    InvitationForbidden,
    InvitationAlreadyPending,
    InvitationRecipientAlreadyMember,
    InvitationEncryptionUnavailable);

public sealed class CreateTenantInvitationHandler(
    PersistTenantInvitation persist
) : IMediatorOutcomeHandler<CreateTenantInvitationRequest, CreateTenantInvitationOutcome>
{
    public async Task<CreateTenantInvitationOutcome> HandleAsync(
        CreateTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await persist(
            new CreateTenantInvitation(
                request.TenantId,
                request.Body.Email,
                request.Body.Role,
                new InvitationActor(request.CurrentUserId)),
            cancellationToken);
    }
}

public sealed class CreateTenantInvitationValidator : AbstractValidator<CreateTenantInvitationRequest>
{
    public CreateTenantInvitationValidator()
    {
        RuleFor(request => request.TenantId).RouteObjectId();
        RuleFor(request => request.Body.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(request => request.Body.Role).IsInEnum();
    }
}

public static class CreateTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapCreateTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{TenantId}/invitations",
                async (
                    [AsParameters] CreateTenantInvitationRequest request, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(request, ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationDto invitation => Results.Created(
                            $"/api/v1/tenants/{request.TenantId}/invitations/{invitation.Id}",
                            invitation),
                        TenantNotFound error => MapTenantNotFound(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                        InvitationAlreadyPending error => InvitationHttpProblems.Pending(error),
                        InvitationRecipientAlreadyMember error => InvitationHttpProblems.AlreadyMember(error),
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("CreateTenantInvitation");
        return app;
    }
}
