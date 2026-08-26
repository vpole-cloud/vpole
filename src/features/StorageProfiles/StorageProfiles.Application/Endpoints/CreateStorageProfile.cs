using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Persistence.Mutations;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record CreateStorageProfileRequestBody(
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<StorageProfileTargetMappingRequestBody> TargetMappings,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits)
    : StorageProfileMutationRequestBody(
        DisplayName,
        Description,
        MediaType,
        PerformanceTier,
        IsEnabled,
        IsDefault,
        TargetMappings,
        AllowedResourceTypes,
        TenantAccess,
        SizeLimits);

public sealed record CreateStorageProfileRequest(
    [FromBody] CreateStorageProfileRequestBody Body)
    : IMediatorRequest<CreateStorageProfileOutcome>;

public union CreateStorageProfileError(StorageProfileMutationValidationError);

public union CreateStorageProfileOutcome(
    StorageProfileDto,
    CreateStorageProfileError);

public sealed class CreateStorageProfileHandler(
    ValidateStorageProfileTargetMappings validateTargetMappings,
    PersistCreatedStorageProfile persistCreatedStorageProfile,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateStorageProfileRequest, CreateStorageProfileOutcome>
{
    public async Task<CreateStorageProfileOutcome> HandleAsync(
        CreateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var profile = request.Body.ToDomain(
            new StorageProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now));
        var targetValidation = await validateTargetMappings(profile, cancellationToken);
        if (targetValidation is Err<StorageProfileMutationValidationError> error)
        {
            return new CreateStorageProfileError(error.Value);
        }

        var persisted = await persistCreatedStorageProfile(profile, cancellationToken);
        return persisted.ToDto();
    }
}

public sealed class CreateStorageProfileRequestValidator
    : AbstractValidator<CreateStorageProfileRequest>
{
    public CreateStorageProfileRequestValidator() =>
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new CreateStorageProfileRequestBodyValidator());
}

public sealed class CreateStorageProfileRequestBodyValidator
    : StorageProfileMutationRequestBodyValidator<CreateStorageProfileRequestBody>;

public static class CreateStorageProfileEndpoint
{
    public static IEndpointRouteBuilder MapCreateStorageProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/profiles", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateStorageProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    StorageProfileDto profile => Results.Created(
                        $"/api/v1/platform/storage/profiles/{profile.Id}",
                        profile),
                    CreateStorageProfileError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateStorageProfile")
            .RequireAuthorization(Policy.Admin)
            .Produces<StorageProfileDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
