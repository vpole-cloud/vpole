using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Mediator;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record GetComputeProfilesRequest(
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 50)
    : IMediatorRequest<GetComputeProfilesOutcome>;

public union GetComputeProfilesOutcome(ComputeProfilePageDto);

public sealed class GetComputeProfilesHandler(
    LoadComputeProfilePage loadComputeProfilePage)
    : IMediatorOutcomeHandler<GetComputeProfilesRequest, GetComputeProfilesOutcome>
{
    public async Task<GetComputeProfilesOutcome> HandleAsync(
        GetComputeProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadComputeProfilePage(
            new ComputeProfileListQuery(request.PageNumber, request.PageSize),
            cancellationToken);
        return new ComputeProfilePageDto(
            page.Items.Select(profile => profile.ToDto()).ToArray(),
            request.PageNumber,
            request.PageSize,
            page.TotalCount);
    }
}

public sealed class GetComputeProfilesRequestValidator
    : AbstractValidator<GetComputeProfilesRequest>
{
    public GetComputeProfilesRequestValidator()
    {
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 200);
    }
}

public static class GetComputeProfilesEndpoint
{
    public static IEndpointRouteBuilder MapGetComputeProfilesEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetComputeProfilesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfilePageDto page => Results.Ok(page)
                });
            })
            .WithName("GetComputeProfiles")
            .Produces<ComputeProfilePageDto>();
        return app;
    }
}
