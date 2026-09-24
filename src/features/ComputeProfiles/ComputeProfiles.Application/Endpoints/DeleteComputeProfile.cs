using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.ComputeProfiles.Application.Common.ComputeProfileHttpMappings;
using static Pole.Features.ComputeProfiles.Contracts.HttpMapping.ComputeProfileContractHttpMappings;
using static Pole.Features.DeploymentTargets.Contracts.HttpMapping.DeploymentTargetContractHttpMappings;
using static Pole.Features.ResourceDeployments.Contracts.HttpMapping.ResourceDeploymentContractHttpMappings;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record DeleteComputeProfileRequest(
    [FromRoute(Name = "computeProfileId")] string ComputeProfileId,
    [FromQuery] long Revision)
    : IMediatorRequest<DeleteComputeProfileOutcome>;

public union DeleteComputeProfileOutcome(
    ComputeProfileDeleted,
    ComputeProfileNotFound,
    ComputeProfileRevisionConflict,
    DefaultComputeProfileCannotBeDeleted,
    DeploymentTargetReferenceChangeRejected,
    ComputeProfileReferencedByResourceDeployment);

public sealed class DeleteComputeProfileHandler(
    DeleteComputeProfile deleteComputeProfile)
    : IMediatorOutcomeHandler<DeleteComputeProfileRequest, DeleteComputeProfileOutcome>
{
    public Task<DeleteComputeProfileOutcome> HandleAsync(
        DeleteComputeProfileRequest request,
        CancellationToken cancellationToken = default)
        => deleteComputeProfile(
            request.ComputeProfileId,
            request.Revision,
            cancellationToken);
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfileDeleted => Results.NoContent(),
                    ComputeProfileNotFound error => MapNotFound(error),
                    ComputeProfileRevisionConflict error => MapRevisionConflict(error),
                    DefaultComputeProfileCannotBeDeleted error => MapDefaultDeletionRejected(error),
                    DeploymentTargetReferenceChangeRejected error => MapReferenceChangeRejected(error),
                    ComputeProfileReferencedByResourceDeployment error => MapComputeProfileReferenced(error)
                });
            })
            .WithName("DeleteComputeProfile")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
