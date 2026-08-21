using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record CreateComputeProfileRequestBody(
    string DisplayName,
    string Description,
    ComputeResourceRequirementsRequestBody Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceRequestBody> ExtendedResources,
    IReadOnlyCollection<string> AllowedResourceTypes,
    ComputeProfileTenantAccessRequestBody TenantAccess,
    IReadOnlyCollection<ComputeProfileTargetMappingRequestBody> TargetMappings,
    bool IsEnabled,
    bool IsDefault)
    : ComputeProfileMutationRequestBody(
        DisplayName,
        Description,
        Resources,
        Architecture,
        ExtendedResources,
        AllowedResourceTypes,
        TenantAccess,
        TargetMappings,
        IsEnabled,
        IsDefault);

public sealed record CreateComputeProfileRequest(
    [FromBody] CreateComputeProfileRequestBody Body)
    : IMediatorRequest<CreateComputeProfileOutcome>;

public union CreateComputeProfileError(
    ComputeProfileMutationValidationError,
    PersistCreatedComputeProfileError);

public union CreateComputeProfileOutcome(
    ComputeProfileDto,
    CreateComputeProfileError);

public sealed class CreateComputeProfileHandler(
    ValidateComputeProfileTenantAccess validateTenantAccess,
    ValidateComputeProfileTargetMappings validateTargetMappings,
    PersistCreatedComputeProfile persistCreatedComputeProfile,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateComputeProfileRequest, CreateComputeProfileOutcome>
{
    public async Task<CreateComputeProfileOutcome> HandleAsync(
        CreateComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = request.Body.ToDomain(
            new Pole.Features.ComputeProfiles.Domain.ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        var tenantValidation = await validateTenantAccess(
            profile.TenantAccess,
            cancellationToken);
        if (tenantValidation is Err<SelectedComputeProfileTenantsNotFound> tenantError)
        {
            return new CreateComputeProfileError(
                new ComputeProfileMutationValidationError(tenantError.Value));
        }
        var targetValidation = await validateTargetMappings(profile, cancellationToken);
        if (targetValidation is Err<ComputeProfileTargetMappingsInvalid> targetError)
        {
            return new CreateComputeProfileError(
                new ComputeProfileMutationValidationError(targetError.Value));
        }

        var persisted = await persistCreatedComputeProfile(profile, cancellationToken);
        return persisted.Match(
            CreateComputeProfileOutcome (created) => created.ToDto(),
            CreateComputeProfileOutcome (error) => new CreateComputeProfileError(error));
    }
}

public sealed class CreateComputeProfileRequestValidator
    : AbstractValidator<CreateComputeProfileRequest>
{
    public CreateComputeProfileRequestValidator() =>
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new CreateComputeProfileRequestBodyValidator());
}

public sealed class CreateComputeProfileRequestBodyValidator
    : ComputeProfileMutationRequestBodyValidator<CreateComputeProfileRequestBody>;

public static class CreateComputeProfileEndpoint
{
    public static IEndpointRouteBuilder MapCreateComputeProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfileDto profile => Results.Created(
                        $"/api/v1/platform/compute-profiles/{profile.Id}",
                        profile),
                    CreateComputeProfileError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateComputeProfile")
            .Produces<ComputeProfileDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
