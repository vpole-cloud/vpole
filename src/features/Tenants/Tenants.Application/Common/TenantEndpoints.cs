// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Endpoints;
using Pole.Features.Tenants.Application.Endpoints.Members;
using Pole.Features.Tenants.Application.Endpoints.Invitations;

namespace Pole.Features.Tenants.Application.Common;

public static class TenantEndpoints
{
    public const string Route = "/tenants";

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAcceptTenantInvitationEndpoint();
        app.MapPreviewTenantInvitationEndpoint();
        app.MapStartInvitationOnboardingEndpoint();
        app.MapGroup(Route)
            .WithoutTenantContext()
            .WithTags("Tenants")
            .RequireAuthorization()
            .MapGetTenantsEndpoint()
            .MapCreateTenantEndpoint()
            .MapGetTenantMembersEndpoint()
            .MapCreateTenantInvitationEndpoint()
            .MapGetTenantInvitationsEndpoint()
            .MapRevokeTenantInvitationEndpoint()
            .MapResendTenantInvitationEndpoint()
            .MapUpdateTenantInvitationEndpoint()
            .MapUpdateTenantMemberEndpoint()
            .MapDeleteTenantMemberEndpoint();

        return app;
    }
}
