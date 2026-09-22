using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record DeleteSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteSourceBuildConfigurationOutcome>, ITenantWriteOperation;

public sealed record SourceBuildConfigurationDeleted(string ConfigurationId);

public union DeleteSourceBuildConfigurationOutcome(
    SourceBuildConfigurationDeleted,
    DeleteSourceBuildConfigurationError);

public sealed class DeleteSourceBuildConfigurationHandler(
    DeleteSourceBuildConfiguration deleteSourceBuildConfiguration)
    : IMediatorOutcomeHandler<
        DeleteSourceBuildConfigurationRequest,
        DeleteSourceBuildConfigurationOutcome>
{
    public async Task<DeleteSourceBuildConfigurationOutcome> HandleAsync(
        DeleteSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var deleted = await deleteSourceBuildConfiguration(
            request.TenantId,
            request.ConfigurationId,
            cancellationToken);
        return deleted switch
        {
            Ok<Unit> => new SourceBuildConfigurationDeleted(request.ConfigurationId),
            Err<DeleteSourceBuildConfigurationError> error => error.Value
        };
    }
}

public sealed class DeleteSourceBuildConfigurationRequestValidator
    : AbstractValidator<DeleteSourceBuildConfigurationRequest>
{
    public DeleteSourceBuildConfigurationRequestValidator() =>
        RuleFor(item => item.ConfigurationId).MongoObjectId("Source build configuration ID");
}

public static class DeleteSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSourceBuildConfigurationEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{ConfigurationId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildConfigurationDeleted => Results.NoContent(),
                    DeleteSourceBuildConfigurationError error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteSourceBuildConfiguration")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
