using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Persistence.Mutations;
using Pole.Features.StorageProfiles.Application.Persistence.Reads;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.StorageProfiles.Application.Common.StorageProfileHttpMappings;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record UpdateStorageProfileRequestBody(
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<StorageProfileTargetMappingRequestBody> TargetMappings,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits,
    long Revision)
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

public sealed record UpdateStorageProfileRequest(
    [FromRoute] string Id,
    [FromBody] UpdateStorageProfileRequestBody Body)
    : IMediatorRequest<UpdateStorageProfileOutcome>;

public union UpdateStorageProfileOutcome(
    StorageProfileDto,
    StorageProfileNotFound,
    StorageProfileRevisionConflict,
    StorageProfileTargetMappingsInvalid,
    KubernetesStorageDiscoveryUnavailable);

public sealed class UpdateStorageProfileHandler(
    LoadStorageProfileById loadStorageProfileById,
    ValidateStorageProfileTargetMappings validateTargetMappings,
    PersistUpdatedStorageProfile persistUpdatedStorageProfile,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateStorageProfileRequest, UpdateStorageProfileOutcome>
{
    public async Task<UpdateStorageProfileOutcome> HandleAsync(
        UpdateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await loadStorageProfileById(request.Id, cancellationToken);
        if (existing is null)
            return new StorageProfileNotFound(request.Id);
        if (existing.Revision != request.Body.Revision)
            return new StorageProfileRevisionConflict(request.Id);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (now < existing.UpdatedAtUtc)
            now = existing.UpdatedAtUtc;
        var updated = request.Body.Reconfigure(existing, now);
        if (updated.Revision == existing.Revision)
            return existing.ToDto();

        var targetValidation = await validateTargetMappings(updated, cancellationToken);
        switch (targetValidation)
        {
            case StorageProfileTargetMappingsInvalid error:
                return error;
            case KubernetesStorageDiscoveryUnavailable error:
                return error;
        }

        var persisted = await persistUpdatedStorageProfile(
            existing,
            updated,
            cancellationToken);
        return persisted switch
        {
            StorageProfile profile => profile.ToDto(),
            StorageProfileRevisionConflict error => error
        };
    }
}

public sealed class UpdateStorageProfileRequestValidator
    : AbstractValidator<UpdateStorageProfileRequest>
{
    public UpdateStorageProfileRequestValidator()
    {
        RuleFor(request => request.Id)
            .MongoObjectId("Storage profile ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new UpdateStorageProfileRequestBodyValidator());
    }
}

public sealed class UpdateStorageProfileRequestBodyValidator
    : StorageProfileMutationRequestBodyValidator<UpdateStorageProfileRequestBody>
{
    public UpdateStorageProfileRequestBodyValidator() =>
        RuleFor(body => body.Revision)
            .GreaterThan(0);
}

public static class UpdateStorageProfileEndpoint
{
    public static IEndpointRouteBuilder MapUpdateStorageProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/profiles/{Id}", async (
                IMediator mediator,
                [AsParameters] UpdateStorageProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    StorageProfileDto profile => Results.Ok(profile),
                    StorageProfileNotFound error => MapNotFound(error),
                    StorageProfileRevisionConflict error => MapRevisionConflict(error),
                    StorageProfileTargetMappingsInvalid error => MapTargetMappingsInvalid(error),
                    KubernetesStorageDiscoveryUnavailable error => MapDiscoveryUnavailable(error)
                });
            })
            .WithName("UpdateStorageProfile")
            .RequireAuthorization(Policy.Admin)
            .Produces<StorageProfileDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
