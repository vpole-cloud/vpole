using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Contracts.HttpMapping;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.SourceBuilds.Application.Common.SourceBuildHttpMappings;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record CreateSourceBuildConfigurationRequest(
    [FromBody] CreateSourceBuildConfigurationBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<CreateSourceBuildConfigurationOutcome>, ITenantWriteOperation;

public sealed record CreateSourceBuildConfigurationBody(
    string TechnicalName,
    string SourceRepositoryId,
    string WebServiceId,
    SourceBuildRecipeDto Recipe
);

public union CreateSourceBuildConfigurationOutcome(
    SourceBuildConfigurationDto,
    SourceRepositoryBuildNotFound,
    SourceBuildConfigurationWebServiceNotFound,
    SourceBuildConfigurationAlreadyExists,
    SourceBuildConfigurationNameConflict,
    SourceBuildConfigurationRepositoryChanged);

public sealed class CreateSourceBuildConfigurationHandler(
    FindSourceRepositoryBuild findSourceRepositoryBuild,
    CreateSourceBuildConfiguration createSourceBuildConfiguration,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<CreateSourceBuildConfigurationRequest, CreateSourceBuildConfigurationOutcome>
{
    public async Task<CreateSourceBuildConfigurationOutcome> HandleAsync(
        CreateSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceRepository = await findSourceRepositoryBuild(
            request.TenantId,
            request.Body.SourceRepositoryId,
            cancellationToken);
        return sourceRepository switch
        {
            SourceRepositoryBuildNotFound notFound => notFound,
            SourceRepositoryBuildSnapshot success => await CreateAsync(
                request,
                success,
                createSourceBuildConfiguration,
                timeProvider,
                cancellationToken),
        };
    }

    private static async Task<CreateSourceBuildConfigurationOutcome> CreateAsync(
        CreateSourceBuildConfigurationRequest request,
        SourceRepositoryBuildSnapshot sourceRepository,
        CreateSourceBuildConfiguration createSourceBuildConfiguration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var configuration = SourceBuildConfigurationFactory.Create(
            request.TenantId,
            request.Body.TechnicalName,
            sourceRepository.Id,
            request.Body.WebServiceId,
            request.Body.Recipe.ToDomain(),
            new ManualSourceBuildConfigurationOrigin(request.CurrentUserId.Value),
            new SourceBuildConfigurationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var created = await createSourceBuildConfiguration(
            new CreateSourceBuildConfigurationCommand(configuration, sourceRepository.Revision),
            cancellationToken);
        return created switch
        {
            SourceBuildConfiguration success => success.ToDto(),
            SourceBuildConfigurationAlreadyExists existsError => existsError,
            SourceBuildConfigurationWebServiceNotFound missing => missing,
            SourceBuildConfigurationNameConflict conflict => conflict,
            SourceBuildConfigurationRepositoryChanged conflict => conflict,
        };
    }
}

public sealed class CreateSourceBuildConfigurationRequestValidator
    : AbstractValidator<CreateSourceBuildConfigurationRequest>
{
    public CreateSourceBuildConfigurationRequestValidator() => RuleFor(item => item.Body)
        .NotNull()
        .SetValidator(new CreateSourceBuildConfigurationBodyValidator());
}

public sealed class CreateSourceBuildConfigurationBodyValidator : AbstractValidator<CreateSourceBuildConfigurationBody>
{
    public CreateSourceBuildConfigurationBodyValidator()
    {
        RuleFor(item => item.TechnicalName).Must(ResourceTechnicalName.IsValid);
        RuleFor(item => item.Recipe).NotNull().SetValidator(new SourceBuildRecipeDtoValidator());
        RuleFor(item => item.SourceRepositoryId).NotEmpty().MongoObjectId("Source repository ID");
        RuleFor(item => item.WebServiceId).NotEmpty().MongoObjectId("Web service ID");
    }
}

public static class CreateSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapCreateSourceBuildConfigurationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateSourceBuildConfigurationRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        SourceBuildConfigurationDto configuration => Results.Created(
                            $"/api/v1/source-build-configurations/{configuration.Id}",
                            configuration),
                        SourceRepositoryBuildNotFound error => GitProviderContractHttpMappings
                            .MapSourceRepositoryBuildNotFound(error),
                        SourceBuildConfigurationNameConflict error => MapConfigurationNameConflict(error),
                        SourceBuildConfigurationRepositoryChanged error => MapConfigurationRepositoryChanged(error),
                        SourceBuildConfigurationAlreadyExists error => MapConfigurationAlreadyExists(error),
                        SourceBuildConfigurationWebServiceNotFound error => MapWebServiceNotFound(error),
                    });
                })
            .WithName("CreateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
