// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record ManagePrivateAccessGrantsRequest(
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue ActorUserId
) : IMediatorRequest<ManagePrivateAccessGrantsOutcome>, ITenantReadOperation;

public sealed record ManagedPrivateAccessGrantList(
    IReadOnlyList<PrivateAccessGrantDto> Items
);

public union ManagePrivateAccessGrantsOutcome(ManagedPrivateAccessGrantList, PrivateAccessManageRoleRequired);

public sealed class ManagePrivateAccessGrantsHandler(
    LoadTenantMember loadTenantMember,
    LoadTenantPrivateAccessGrants loadGrants
) : IMediatorOutcomeHandler<ManagePrivateAccessGrantsRequest, ManagePrivateAccessGrantsOutcome>
{
    public async Task<ManagePrivateAccessGrantsOutcome> HandleAsync(
        ManagePrivateAccessGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = request.ActorUserId.Value;
        if (await loadTenantMember(
                new LoadTenantMemberRequest(TenantId: request.TenantId, UserId: actor),
                cancellationToken: cancellationToken) is not TenantMemberSnapshot
            {
                Role: >= TenantMembershipRole.Admin,
            })
            return new PrivateAccessManageRoleRequired();
        var grants = await loadGrants(request.TenantId, cancellationToken);
        return new ManagedPrivateAccessGrantList(grants.Select(item => item.ToDto()).ToArray());
    }
}

public sealed class ManagePrivateAccessGrantsValidator : AbstractValidator<ManagePrivateAccessGrantsRequest>;

public static class ManagePrivateAccessGrantsEndpoint
{
    public static IEndpointRouteBuilder MapManagePrivateAccessGrantsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/manage",
                async (
                        IMediator mediator, [AsParameters] ManagePrivateAccessGrantsRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        ManagedPrivateAccessGrantList list => Results.Ok(list.Items),
                        PrivateAccessManageRoleRequired error => PrivateAccessHttpProblemMappings.Map(error),
                    }))
            .WithName("ManagePrivateAccessGrants")
            .Produces<IReadOnlyList<PrivateAccessGrantDto>>();
        return app;
    }
}
