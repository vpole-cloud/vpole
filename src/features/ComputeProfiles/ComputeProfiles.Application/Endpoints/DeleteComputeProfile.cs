using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record DeleteComputeProfileRequest(
    [FromRoute(Name = "computeProfileId")] string ComputeProfileId,
    [FromQuery] long Revision)
    : IMediatorRequest<string>;

public sealed class DeleteComputeProfileHandler(
    DeleteComputeProfile deleteComputeProfile)
    : IMediatorHandler<DeleteComputeProfileRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await deleteComputeProfile(
            request.ComputeProfileId,
            request.Revision,
            cancellationToken);
        return result is ISuccess
            ? request.ComputeProfileId
            : (Failure)result;
    }
}

public sealed class DeleteComputeProfileRequestValidator
    : AbstractValidator<DeleteComputeProfileRequest>
{
    public DeleteComputeProfileRequestValidator()
    {
        RuleFor(request => request.ComputeProfileId)
            .MongoObjectId("Compute profile ID");
        RuleFor(request => request.Revision).GreaterThan(0);
    }
}

public static class DeleteComputeProfileEndpoint
{
    public static IEndpointRouteBuilder MapDeleteComputeProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{computeProfileId}", async (
                IMediator mediator,
                [AsParameters] DeleteComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteComputeProfile")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
