using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record GetComputeProfileRequest(
    [FromRoute(Name = "computeProfileId")] string ComputeProfileId)
    : IMediatorRequest<GetComputeProfileOutcome>;

public union GetComputeProfileOutcome(
    ComputeProfileDto,
    ComputeProfileNotFound);

public sealed class GetComputeProfileHandler(
    LoadComputeProfileById loadComputeProfileById)
    : IMediatorOutcomeHandler<GetComputeProfileRequest, GetComputeProfileOutcome>
{
    public async Task<GetComputeProfileOutcome> HandleAsync(
        GetComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = await loadComputeProfileById(
            request.ComputeProfileId,
            cancellationToken);
        return profile is null
            ? new ComputeProfileNotFound(request.ComputeProfileId)
            : profile.ToDto();
    }
}

public sealed class GetComputeProfileRequestValidator
    : AbstractValidator<GetComputeProfileRequest>
{
    public GetComputeProfileRequestValidator() =>
        RuleFor(request => request.ComputeProfileId)
            .MongoObjectId("Compute profile ID");
}

public static class GetComputeProfileEndpoint
{
    public static IEndpointRouteBuilder MapGetComputeProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{computeProfileId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfileDto profile => Results.Ok(profile),
                    ComputeProfileNotFound notFound => httpProblems.Map(notFound).ToHttpResult()
                });
            })
            .WithName("GetComputeProfile")
            .Produces<ComputeProfileDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
