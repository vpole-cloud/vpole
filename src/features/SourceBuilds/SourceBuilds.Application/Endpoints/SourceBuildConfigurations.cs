using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public static class SourceBuildConfigurations
{
    public static IEndpointRouteBuilder MapSourceBuildConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceBuildConfigurationsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildConfigurations")
            .Produces<IReadOnlyList<SourceBuildConfigurationDto>>();

        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(value => Results.Created(
                    $"/api/v1/source-build-configurations/{value.Id}",
                    value));
            })
            .WithName("CreateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>(StatusCodes.Status201Created);

        app.MapPut("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] UpdateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>();

        app.MapDelete("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] DeleteSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceBuildConfiguration")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record GetSourceBuildConfigurationsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<SourceBuildConfigurationDto>>, ITenantReadOperation;

public sealed record CreateSourceBuildConfigurationRequest(
    [FromBody] CreateSourceBuildConfigurationBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<SourceBuildConfigurationDto>, ITenantWriteOperation;

public sealed record CreateSourceBuildConfigurationBody(
    string SourceRepositoryId,
    string WebServiceId);

public sealed record UpdateSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromBody] UpdateSourceBuildConfigurationBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceBuildConfigurationDto>, ITenantWriteOperation;

public sealed record UpdateSourceBuildConfigurationBody(SourceBuildRecipeDto Recipe);

public sealed record DeleteSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class GetSourceBuildConfigurationsHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<GetSourceBuildConfigurationsRequest, IReadOnlyList<SourceBuildConfigurationDto>>
{
    public async Task<Result<IReadOnlyList<SourceBuildConfigurationDto>>> HandleAsync(
        GetSourceBuildConfigurationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurations = await dbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == request.TenantId)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return configurations.Select(item => item.ToDto()).ToArray();
    }
}

public sealed class CreateSourceBuildConfigurationHandler(
    ISourceBuildDbContext dbContext,
    IGitProviderDbContext gitProviderDbContext,
    IWebServiceDbContext webServiceDbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateSourceBuildConfigurationRequest, SourceBuildConfigurationDto>
{
    public async Task<Result<SourceBuildConfigurationDto>> HandleAsync(
        CreateSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceRepository = await gitProviderDbContext.SourceRepositories
            .Find(item =>
                item.TenantId == request.TenantId &&
                item.Id == request.Body.SourceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceRepository is null)
            return Failures.NotFound("Source repository not found.", request.Body.SourceRepositoryId);

        var webServiceExists = await webServiceDbContext.WebServices
            .Find(item => item.TenantId == request.TenantId && item.Id == request.Body.WebServiceId)
            .AnyAsync(cancellationToken);
        if (!webServiceExists)
            return Failures.NotFound("Web service not found.", request.Body.WebServiceId);

        var alreadyConfigured = await dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == request.TenantId &&
                item.WebServiceId == request.Body.WebServiceId)
            .AnyAsync(cancellationToken);
        if (alreadyConfigured)
            return Failures.Conflict("This web service already has a source build configuration.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var configuration = SourceBuildConfigurationFactory.Create(
            request.TenantId,
            sourceRepository.Id,
            request.Body.WebServiceId,
            sourceRepository.SelectedPlan.ToBuildRecipe(),
            request.CurrentUserId.Value,
            new SourceBuildConfigurationFactory.CreateOptions(
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                now));
        var inserted = await dbContext.SourceBuildConfigurations.InsertOneAsResultAsync(
            configuration,
            () => Failures.Conflict(
                "This web service already has a source build configuration."),
            cancellationToken);
        return inserted.TryGetValue(out var value, out var failure)
            ? value.ToDto()
            : failure;
    }
}

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
        if (!string.Equals(
                existing.Recipe.CandidateKey,
                request.Body.Recipe.CandidateKey,
                StringComparison.Ordinal))
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

public sealed class DeleteSourceBuildConfigurationHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<DeleteSourceBuildConfigurationRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var terminalStates = Builders<SourceBuildJob>.Filter.Or(
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, SucceededSourceBuild>(item => item.State),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, FailedSourceBuild>(item => item.State));
        var hasActiveBuilds = await dbContext.SourceBuildJobs
            .Find(Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.Eq(item => item.TenantId, request.TenantId),
                Builders<SourceBuildJob>.Filter.Eq(item => item.ConfigurationId, request.ConfigurationId),
                Builders<SourceBuildJob>.Filter.Not(terminalStates)))
            .AnyAsync(cancellationToken);
        if (hasActiveBuilds)
            return Failures.Conflict("Wait for active builds to finish before deleting this configuration.");

        var deleted = await dbContext.SourceBuildConfigurations.DeleteOneAsync(
            item => item.TenantId == request.TenantId && item.Id == request.ConfigurationId,
            cancellationToken);
        return deleted.DeletedCount == 0
            ? Failures.NotFound("Source build configuration not found.", request.ConfigurationId)
            : request.ConfigurationId;
    }
}

public sealed class CreateSourceBuildConfigurationRequestValidator
    : AbstractValidator<CreateSourceBuildConfigurationRequest>
{
    public CreateSourceBuildConfigurationRequestValidator()
    {
        RuleFor(item => item.Body)
            .NotNull()
            .SetValidator(new CreateSourceBuildConfigurationBodyValidator());
    }
}

public sealed class CreateSourceBuildConfigurationBodyValidator
    : AbstractValidator<CreateSourceBuildConfigurationBody>
{
    public CreateSourceBuildConfigurationBodyValidator()
    {
        RuleFor(item => item.SourceRepositoryId).NotEmpty();
        RuleFor(item => item.WebServiceId).NotEmpty();
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

public sealed class SourceBuildRecipeDtoValidator : AbstractValidator<SourceBuildRecipeDto>
{
    public SourceBuildRecipeDtoValidator()
    {
        RuleFor(item => item.CandidateKey).NotEmpty().MaximumLength(500);
        RuleFor(item => item.SourceRoot).MaximumLength(1000);
        RuleFor(item => item.ManifestPath).NotEmpty().MaximumLength(1000);
        RuleFor(item => item.Commands)
            .NotNull()
            .Must(commands => commands is null || commands.Count <= 50);
        RuleForEach(item => item.Commands).NotEmpty().MaximumLength(2000);
        RuleFor(item => item.StartCommand).MaximumLength(2000);
        RuleFor(item => item.Dockerfile).NotEmpty().MaximumLength(100_000);
    }
}
