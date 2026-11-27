// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources;
using Pole.Mediator;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record RepositorySourcePage(
    IReadOnlyList<RepositorySourceView> Items,
    int Page,
    int PageSize,
    bool HasMore
);

public union ListRepositorySourcesOutcome(RepositorySourcePage);

public sealed record ListRepositorySourcesRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50
) : IMediatorRequest<ListRepositorySourcesOutcome>, ITenantReadOperation;

public sealed class ListRepositorySourcesValidator : AbstractValidator<ListRepositorySourcesRequest>
{
    public ListRepositorySourcesValidator()
    {
        RuleFor(item => item.Page).InclusiveBetween(1, 1000000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListRepositorySourcesHandler(
    GitRepositoryQueries queries
) : IMediatorOutcomeHandler<ListRepositorySourcesRequest, ListRepositorySourcesOutcome>
{
    public async Task<ListRepositorySourcesOutcome> HandleAsync(
        ListRepositorySourcesRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();

        var result = await queries.ListAsync(tenantId, request.Page, request.PageSize, cancellationToken);
        return result;
    }
}

public static class ListRepositorySourcesEndpoint
{
    public static IEndpointRouteBuilder MapListRepositorySourcesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                    IMediator mediator, [AsParameters] ListRepositorySourcesRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositorySourcePage value => Results.Ok(value),
                }))
            .WithName("ListRepositorySources");
        return app;
    }
}
