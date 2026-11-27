// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record ListPrivateAccessResourceGrantsRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Kind,
    [FromQuery] string Name,
    [FromQuery] string? DeploymentId,
    [FromQuery] string? EndpointId,
    [FromClaim(Claims.UserId)] ClaimValue SubjectUserId
) : IMediatorRequest<ListPrivateAccessResourceGrantsOutcome>, ITenantReadOperation;

public sealed record PrivateAccessResourceGrantOption(
    string GrantId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTime ExpiresAtUtc
);

public sealed record PrivateAccessResourceGrantOptions(
    IReadOnlyList<PrivateAccessResourceGrantOption> Items
);

public union ListPrivateAccessResourceGrantsOutcome(
    PrivateAccessResourceGrantOptions,
    PrivateAccessSubjectMembershipRequired);

public sealed class ListPrivateAccessResourceGrantsHandler(
    LoadTenantMember loadTenantMember,
    LoadOwnPrivateAccessResourceGrants loadGrants,
    PrivateAccessResourceNameCatalog resources,
    LoadPrivateAccessGrantRoutes routes,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<ListPrivateAccessResourceGrantsRequest, ListPrivateAccessResourceGrantsOutcome>
{
    public async Task<ListPrivateAccessResourceGrantsOutcome> HandleAsync(
        ListPrivateAccessResourceGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var subjectId = request.SubjectUserId.Value;
        if (await loadTenantMember(
                new LoadTenantMemberRequest(TenantId: request.TenantId, UserId: subjectId),
                cancellationToken: cancellationToken) is not TenantMemberSnapshot)
            return new PrivateAccessSubjectMembershipRequired();

        var resource = await resources.ResolveAsync(request.TenantId, request.Kind, request.Name, cancellationToken);
        if (resource is null)
            return new PrivateAccessResourceGrantOptions([]);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var grants = await loadGrants(
            request.TenantId,
            subjectId,
            resource.ResourceType.Value,
            resource.ResourceId,
            now,
            cancellationToken);
        var candidates = grants.Where(grant =>
                (request.DeploymentId is null || grant.ResourceDeploymentId == request.DeploymentId) &&
                (request.EndpointId is null || grant.EndpointId == request.EndpointId))
            .ToArray();
        var available = await routes(request.TenantId, resource.ResourceType, candidates, cancellationToken);

        var options = new List<PrivateAccessResourceGrantOption>();
        foreach (var grant in candidates)
        {
            if (!available.ContainsKey((grant.ResourceId, grant.ResourceDeploymentId, grant.EndpointId)))
                continue;

            options.Add(
                new PrivateAccessResourceGrantOption(
                    grant.Id,
                    grant.ResourceDeploymentId,
                    grant.EndpointId,
                    grant.ExpiresAtUtc));
        }

        return new PrivateAccessResourceGrantOptions(options);
    }
}

public sealed class ListPrivateAccessResourceGrantsRequestValidator
    : AbstractValidator<ListPrivateAccessResourceGrantsRequest>
{
    public ListPrivateAccessResourceGrantsRequestValidator()
    {
        RuleFor(request => request.Kind).NotEmpty().MaximumLength(64);
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
    }
}

public static class ListPrivateAccessResourceGrantsEndpoint
{
    public static IEndpointRouteBuilder MapListPrivateAccessResourceGrantsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Kind}/grants",
                async (
                        IMediator mediator, [AsParameters] ListPrivateAccessResourceGrantsRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        PrivateAccessResourceGrantOptions options => Results.Ok(options),
                        PrivateAccessSubjectMembershipRequired error => PrivateAccessHttpProblemMappings.Map(error),
                    }))
            .WithName("ListPrivateAccessResourceGrants")
            .Produces<PrivateAccessResourceGrantOptions>();
        return app;
    }
}
