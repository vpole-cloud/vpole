// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

public sealed record GetSourceBuildJobsRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] string? ConfigurationId = null,
    [FromQuery] string? WebServiceId = null,
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 20
) : IMediatorRequest<GetSourceBuildJobsOutcome>, ITenantReadOperation;

public union GetSourceBuildJobsOutcome(PaginatedResponse<SourceBuildJobDto>);

public sealed class GetSourceBuildJobsHandler(
    LoadSourceBuildJobPage loadSourceBuildJobPage
) : IMediatorOutcomeHandler<GetSourceBuildJobsRequest, GetSourceBuildJobsOutcome>
{
    public async Task<GetSourceBuildJobsOutcome> HandleAsync(
        GetSourceBuildJobsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadSourceBuildJobPage(
            new SourceBuildJobListQuery(
                request.TenantId,
                request.ConfigurationId,
                request.WebServiceId,
                request.PageNumber,
                request.PageSize),
            cancellationToken);
        return page.Map(item => item.ToDto());
    }
}

public sealed class GetSourceBuildJobsRequestValidator : AbstractValidator<GetSourceBuildJobsRequest>
{
    public GetSourceBuildJobsRequestValidator()
    {
        RuleFor(item => item.ConfigurationId).OptionalMongoObjectId("Source build configuration ID");
        RuleFor(item => item.WebServiceId).OptionalMongoObjectId("Web service ID");
        RuleFor(item => item.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}

public static class GetSourceBuildJobsEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildJobsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                    IMediator mediator, [AsParameters] GetSourceBuildJobsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        PaginatedResponse<SourceBuildJobDto> page => Results.Ok(page),
                    });
                })
            .WithName("GetSourceBuildJobs")
            .Produces<PaginatedResponse<SourceBuildJobDto>>();
        return app;
    }
}
