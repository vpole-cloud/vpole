using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record CreateSourceBuildConfigurationRequest(
    [FromBody] CreateSourceBuildConfigurationBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<CreateSourceBuildConfigurationOutcome>, ITenantWriteOperation;

public sealed record CreateSourceBuildConfigurationBody(
    string SourceRepositoryId,
    string WebServiceId);

public union CreateSourceBuildConfigurationError(
    SourceRepositoryBuildNotFound,
    SourceBuildConfigurationWebServiceNotFound,
    SourceBuildConfigurationAlreadyExists);

public union CreateSourceBuildConfigurationOutcome(
    SourceBuildConfigurationDto,
    CreateSourceBuildConfigurationError);

public sealed class CreateSourceBuildConfigurationHandler(
    FindSourceRepositoryBuild findSourceRepositoryBuild,
    SourceBuildWebServiceExists webServiceExists,
    CreateSourceBuildConfiguration createSourceBuildConfiguration,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        CreateSourceBuildConfigurationRequest,
        CreateSourceBuildConfigurationOutcome>
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
            Err<FindSourceRepositoryBuildError> error => error.Value switch
            {
                SourceRepositoryBuildNotFound notFound =>
                    new CreateSourceBuildConfigurationError(notFound)
            },
            Ok<SourceRepositoryBuildSnapshot> success => await CreateAsync(
                request,
                success.Value,
                webServiceExists,
                createSourceBuildConfiguration,
                timeProvider,
                cancellationToken)
        };
    }

    private static async Task<CreateSourceBuildConfigurationOutcome> CreateAsync(
        CreateSourceBuildConfigurationRequest request,
        SourceRepositoryBuildSnapshot sourceRepository,
        SourceBuildWebServiceExists webServiceExists,
        CreateSourceBuildConfiguration createSourceBuildConfiguration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var exists = await webServiceExists(
            request.TenantId,
            request.Body.WebServiceId,
            cancellationToken);
        if (!exists)
        {
            return new CreateSourceBuildConfigurationError(
                new SourceBuildConfigurationWebServiceNotFound(request.Body.WebServiceId));
        }

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
        var created = await createSourceBuildConfiguration(configuration, cancellationToken);
        return created switch
        {
            Ok<SourceBuildConfiguration> success => success.Value.ToDto(),
            Err<PersistSourceBuildConfigurationError> error => error.Value switch
            {
                SourceBuildConfigurationAlreadyExists existsError => new CreateSourceBuildConfigurationError(existsError),
                SourceBuildConfigurationWebServiceNotFound missing => new CreateSourceBuildConfigurationError(missing)
            }
        };
    }
}

public sealed class CreateSourceBuildConfigurationRequestValidator
    : AbstractValidator<CreateSourceBuildConfigurationRequest>
{
    public CreateSourceBuildConfigurationRequestValidator() =>
        RuleFor(item => item.Body)
            .NotNull()
            .SetValidator(new CreateSourceBuildConfigurationBodyValidator());
}

public sealed class CreateSourceBuildConfigurationBodyValidator
    : AbstractValidator<CreateSourceBuildConfigurationBody>
{
    public CreateSourceBuildConfigurationBodyValidator()
    {
        RuleFor(item => item.SourceRepositoryId)
            .NotEmpty()
            .MongoObjectId("Source repository ID");
        RuleFor(item => item.WebServiceId)
            .NotEmpty()
            .MongoObjectId("Web service ID");
    }
}

public static class CreateSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapCreateSourceBuildConfigurationEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildConfigurationDto configuration => Results.Created(
                        $"/api/v1/source-build-configurations/{configuration.Id}",
                        configuration),
                    CreateSourceBuildConfigurationError error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
