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
using Pole.Features.GitOps.Contracts;
using Pole.Features.Request;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record GetResourceConnectionsRequest
    : IMediatorRequest<GetResourceConnectionsOutcome>, ITenantReadOperation
{
    [FromQuery] public int PageNumber { get; init; } = 1;
    [FromQuery] public int PageSize { get; init; } = 10;
    [FromQuery] public string? SourceResourceType { get; init; }
    [FromQuery] public string? SourceResourceId { get; init; }
    [FromTenantHeader] public string TenantId { get; init; } = "";
}

public union GetResourceConnectionsOutcome(PaginatedResponse<ResourceConnectionDto>);

public sealed class GetResourceConnectionsRequestValidator : AbstractValidator<GetResourceConnectionsRequest>
{
    public GetResourceConnectionsRequestValidator()
    {
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
        When(
            request => request.SourceResourceType is not null || request.SourceResourceId is not null,
            () =>
            {
                RuleFor(request => request.SourceResourceType)
                    .NotEmpty()
                    .Must(value => ResourceTypeId.TryCreate(value, out _));
                RuleFor(request => request.SourceResourceId).NotEmpty().MongoObjectId("Source resource ID");
            });
        RuleFor(request => request)
            .Must(request => request.PageNumber <= 0 || request.PageSize <= 0 ||
                (long)(request.PageNumber - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
    }
}

public sealed class GetResourceConnectionsHandler(
    LoadResourceConnectionsPage loadPage,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetResourceConnectionsRequest, GetResourceConnectionsOutcome>
{
    public async Task<GetResourceConnectionsOutcome> HandleAsync(
        GetResourceConnectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadPage(
            request.TenantId,
            request.PageNumber,
            request.PageSize,
            cancellationToken,
            request.SourceResourceType is null ? null : ResourceTypeId.Create(request.SourceResourceType),
            request.SourceResourceId is null ? null : ObjectId.Parse(request.SourceResourceId).ToString());
        var statuses = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                request.TenantId,
                "ResourceConnection",
                page.Items.Select(item => item.Id).ToArray()),
            cancellationToken);
        return new PaginatedResponse<ResourceConnectionDto>(
            page.Items
                .Select(connection => connection.ToDto() with
                {
                    GitOps = statuses.GetValueOrDefault(connection.Id),
                })
                .ToArray(),
            page.TotalCount,
            request.PageNumber,
            request.PageSize);
    }
}

public static class GetResourceConnectionsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceConnectionsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                        IMediator mediator, [AsParameters] GetResourceConnectionsRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        PaginatedResponse<ResourceConnectionDto> page => Results.Ok(page),
                    }))
            .WithName("GetResourceConnections")
            .Produces<PaginatedResponse<ResourceConnectionDto>>();
        return app;
    }
}
