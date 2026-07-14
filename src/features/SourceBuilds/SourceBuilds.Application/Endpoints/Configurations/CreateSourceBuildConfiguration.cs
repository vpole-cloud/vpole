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
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record CreateSourceBuildConfigurationRequest(
    [FromBody] CreateSourceBuildConfigurationBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<SourceBuildConfigurationDto>, ITenantWriteOperation;

public sealed record CreateSourceBuildConfigurationBody(
    string SourceRepositoryId,
    string WebServiceId);

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
            .Find(item => item.TenantId == request.TenantId && item.Id == request.Body.SourceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceRepository is null)
            return Failures.NotFound("Source repository not found.", request.Body.SourceRepositoryId);

        var webServiceExists = await webServiceDbContext.WebServices
            .Find(item => item.TenantId == request.TenantId && item.Id == request.Body.WebServiceId)
            .AnyAsync(cancellationToken);
        if (!webServiceExists)
            return Failures.NotFound("Web service not found.", request.Body.WebServiceId);

        var alreadyConfigured = await dbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == request.TenantId && item.WebServiceId == request.Body.WebServiceId)
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
            new SourceBuildConfigurationFactory.CreateOptions(MongoDB.Bson.ObjectId.GenerateNewId().ToString(), now));
        var inserted = await dbContext.SourceBuildConfigurations.InsertOneAsResultAsync(
            configuration,
            () => Failures.Conflict("This web service already has a source build configuration."),
            cancellationToken);
        return inserted.TryGetValue(out var value, out var failure)
            ? value.ToDto()
            : failure;
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
        RuleFor(item => item.SourceRepositoryId).NotEmpty();
        RuleFor(item => item.WebServiceId).NotEmpty();
    }
}

public static class CreateSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapCreateSourceBuildConfigurationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(value => Results.Created(
                    $"/api/v1/source-build-configurations/{value.Id}", value));
            })
            .WithName("CreateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>(StatusCodes.Status201Created);
        return app;
    }
}
