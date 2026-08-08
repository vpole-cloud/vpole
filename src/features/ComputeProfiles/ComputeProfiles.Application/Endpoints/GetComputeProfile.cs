using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record GetComputeProfileRequest(
    [FromRoute(Name = "computeProfileId")] string ComputeProfileId)
    : IMediatorRequest<ComputeProfileDto>;

public sealed class GetComputeProfileHandler(
    LoadComputeProfileById loadComputeProfileById)
    : IMediatorHandler<GetComputeProfileRequest, ComputeProfileDto>
{
    public async Task<Result<ComputeProfileDto>> HandleAsync(
        GetComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = await loadComputeProfileById(
            request.ComputeProfileId,
            cancellationToken);
        return profile is null
            ? Failures.NotFound("Compute profile not found.", request.ComputeProfileId)
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
                [AsParameters] GetComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetComputeProfile")
            .Produces<ComputeProfileDto>();
        return app;
    }
}
