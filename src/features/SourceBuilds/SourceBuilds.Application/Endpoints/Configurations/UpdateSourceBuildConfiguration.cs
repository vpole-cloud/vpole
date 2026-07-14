using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record UpdateSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromBody] UpdateSourceBuildConfigurationBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceBuildConfigurationDto>, ITenantWriteOperation;

public sealed record UpdateSourceBuildConfigurationBody(SourceBuildRecipeDto Recipe);

public sealed class UpdateSourceBuildConfigurationHandler(
    ISourceBuildDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateSourceBuildConfigurationRequest, SourceBuildConfigurationDto>
{
    public async Task<Result<SourceBuildConfigurationDto>> HandleAsync(
        UpdateSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<SourceBuildConfiguration>.Filter.And(
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.TenantId, request.TenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.Id, request.ConfigurationId));
        var existing = await dbContext.SourceBuildConfigurations
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return Failures.NotFound("Source build configuration not found.", request.ConfigurationId);
        if (!string.Equals(existing.Recipe.CandidateKey, request.Body.Recipe.CandidateKey, StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The recipe candidate cannot be changed; relink the source repository candidate instead.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await dbContext.SourceBuildConfigurations.FindOneAndUpdateAsync(
            filter,
            Builders<SourceBuildConfiguration>.Update
                .Set(item => item.Recipe, request.Body.Recipe.ToDomain(isCustomized: true))
                .Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<SourceBuildConfiguration> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return updated.ToDto();
    }
}

public sealed class UpdateSourceBuildConfigurationRequestValidator
    : AbstractValidator<UpdateSourceBuildConfigurationRequest>
{
    public UpdateSourceBuildConfigurationRequestValidator()
    {
        RuleFor(item => item.ConfigurationId).NotEmpty();
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
    public static IEndpointRouteBuilder MapUpdateSourceBuildConfigurationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] UpdateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>();
        return app;
    }
}
