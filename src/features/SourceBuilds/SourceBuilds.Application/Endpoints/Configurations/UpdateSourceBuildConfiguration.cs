using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.SourceBuilds.Application.Common.SourceBuildHttpMappings;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record UpdateSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromBody] UpdateSourceBuildConfigurationBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<UpdateSourceBuildConfigurationOutcome>, ITenantWriteOperation;

public sealed record UpdateSourceBuildConfigurationBody(
    SourceBuildRecipeDto Recipe,
    long ExpectedRevision);

public union UpdateSourceBuildConfigurationOutcome(
    SourceBuildConfigurationDto,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationGitOwned,
    SourceBuildConfigurationUpdateConflict);

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
            return new SourceBuildConfigurationNotFound(request.ConfigurationId);
        }
        if (existing.Revision != request.Body.ExpectedRevision)
            return new SourceBuildConfigurationUpdateConflict(existing.Id);

        var updated = await updateSourceBuildConfiguration(
            new(existing, request.Body.Recipe.ToDomain(), timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
        return updated switch
        {
            SourceBuildConfiguration success => success.ToDto(),
            SourceBuildConfigurationNotFound notFound => notFound,
            SourceBuildConfigurationUpdateConflict conflict => conflict,
            SourceBuildConfigurationGitOwned owned => owned
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
    public UpdateSourceBuildConfigurationBodyValidator()
    {
        RuleFor(item => item.ExpectedRevision).GreaterThan(0);
        RuleFor(item => item.Recipe)
            .NotNull()
            .SetValidator(new SourceBuildRecipeDtoValidator());
    }
}

public static class UpdateSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapUpdateSourceBuildConfigurationEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/{ConfigurationId}", async (
                IMediator mediator,
                [AsParameters] UpdateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildConfigurationDto configuration => Results.Ok(configuration),
                    SourceBuildConfigurationNotFound error => MapConfigurationNotFound(error),
                    SourceBuildConfigurationGitOwned error => MapConfigurationGitOwned(error),
                    SourceBuildConfigurationUpdateConflict error => MapConfigurationUpdateConflict(error)
                });
            })
            .WithName("UpdateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
