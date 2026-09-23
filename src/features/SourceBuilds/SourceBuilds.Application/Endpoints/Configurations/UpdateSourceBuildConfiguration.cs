using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record UpdateSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromBody] UpdateSourceBuildConfigurationBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<UpdateSourceBuildConfigurationOutcome>, ITenantWriteOperation;

public sealed record UpdateSourceBuildConfigurationBody(SourceBuildRecipeDto Recipe);

public union UpdateSourceBuildConfigurationError(
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationRecipeCandidateChanged,
    SourceBuildConfigurationUpdateConflict);

public union UpdateSourceBuildConfigurationOutcome(
    SourceBuildConfigurationDto,
    UpdateSourceBuildConfigurationError);

public sealed class UpdateSourceBuildConfigurationHandler(
    LoadSourceBuildConfiguration loadSourceBuildConfiguration,
    UpdateSourceBuildConfiguration updateSourceBuildConfiguration,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        UpdateSourceBuildConfigurationRequest,
        UpdateSourceBuildConfigurationOutcome>
{
    public async Task<UpdateSourceBuildConfigurationOutcome> HandleAsync(
        UpdateSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await loadSourceBuildConfiguration(
            request.TenantId,
            request.ConfigurationId,
            cancellationToken);
        if (existing is null)
        {
            return new UpdateSourceBuildConfigurationError(
                new SourceBuildConfigurationNotFound(request.ConfigurationId));
        }
        if (!string.Equals(
                existing.Recipe.CandidateKey,
                request.Body.Recipe.CandidateKey,
                StringComparison.Ordinal))
        {
            return new UpdateSourceBuildConfigurationError(
                new SourceBuildConfigurationRecipeCandidateChanged(existing.Id));
        }

        var updated = await updateSourceBuildConfiguration(
            existing,
            request.Body.Recipe.ToDomain(isCustomized: true),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return updated switch
        {
            Ok<SourceBuildConfiguration> success => success.Value.ToDto(),
            Err<PersistUpdatedSourceBuildConfigurationError> error => error.Value switch
            {
                SourceBuildConfigurationNotFound notFound =>
                    new UpdateSourceBuildConfigurationError(notFound),
                SourceBuildConfigurationUpdateConflict conflict =>
                    new UpdateSourceBuildConfigurationError(conflict)
            }
        };
    }
}

public sealed class UpdateSourceBuildConfigurationRequestValidator
    : AbstractValidator<UpdateSourceBuildConfigurationRequest>
{
    public UpdateSourceBuildConfigurationRequestValidator()
    {
        RuleFor(item => item.ConfigurationId).MongoObjectId("Source build configuration ID");
        RuleFor(item => item.Body)
            .NotNull()
            .SetValidator(new UpdateSourceBuildConfigurationBodyValidator());
    }
}

public sealed class UpdateSourceBuildConfigurationBodyValidator
    : AbstractValidator<UpdateSourceBuildConfigurationBody>
{
    public UpdateSourceBuildConfigurationBodyValidator() =>
        RuleFor(item => item.Recipe)
            .NotNull()
            .SetValidator(new SourceBuildRecipeDtoValidator());
}

public static class UpdateSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapUpdateSourceBuildConfigurationEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/{ConfigurationId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildConfigurationDto configuration => Results.Ok(configuration),
                    UpdateSourceBuildConfigurationError error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("UpdateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
