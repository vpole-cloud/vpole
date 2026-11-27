// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Validation;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.Workloads;
using static Pole.Features.StorageProfiles.Application.Common.StorageProfileHttpMappings;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record CreateStorageProfileRequestBody(
    string TechnicalName,
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<StorageProfileTargetMappingRequestBody> TargetMappings,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits
) : StorageProfileMutationRequestBody(
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
    [FromBody] CreateStorageProfileRequestBody Body
) : IMediatorRequest<CreateStorageProfileOutcome>;

public union CreateStorageProfileOutcome(
    StorageProfileDto,
    StorageProfileTechnicalNameConflict,
    StorageProfileRevisionConflict,
    StorageProfileTargetMappingsInvalid,
    KubernetesStorageDiscoveryUnavailable);

public sealed class CreateStorageProfileHandler(
    ValidateStorageProfileTargetMappings validateTargetMappings,
    PersistCreatedStorageProfile persistCreatedStorageProfile,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<CreateStorageProfileRequest, CreateStorageProfileOutcome>
{
    public async Task<CreateStorageProfileOutcome> HandleAsync(
        CreateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var profile = request.Body.ToDomain(
            request.Body.TechnicalName,
            new StorageProfileFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var targetValidation = await validateTargetMappings(profile, cancellationToken);
        switch (targetValidation)
        {
            case StorageProfileTargetMappingsInvalid error:
                return error;
            case KubernetesStorageDiscoveryUnavailable error:
                return error;
        }

        var persisted = await persistCreatedStorageProfile(profile, cancellationToken);
        return persisted switch
        {
            StorageProfile profileResult => profileResult.ToDto(),
            StorageProfileTechnicalNameConflict conflict => conflict,
            StorageProfileRevisionConflict conflict => conflict,
        };
    }
}

public sealed class CreateStorageProfileRequestValidator : AbstractValidator<CreateStorageProfileRequest>
{
    public CreateStorageProfileRequestValidator() => RuleFor(request => request.Body)
        .NotNull()
        .SetValidator(new CreateStorageProfileRequestBodyValidator());
}

public sealed class CreateStorageProfileRequestBodyValidator
    : StorageProfileMutationRequestBodyValidator<CreateStorageProfileRequestBody>
{
    public CreateStorageProfileRequestBodyValidator() => RuleFor(body => body.TechnicalName)
        .Must(ResourceTechnicalName.IsValid)
        .WithMessage("A lowercase technical name starting with a letter is required (maximum 63 characters).");
}

public static class CreateStorageProfileEndpoint
{
    public static IEndpointRouteBuilder MapCreateStorageProfileEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/profiles",
                async (
                    IMediator mediator, [AsParameters] CreateStorageProfileRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        StorageProfileDto profile => Results.Created(
                            $"/api/v1/platform/storage/profiles/{profile.Id}",
                            profile),
                        StorageProfileTechnicalNameConflict error => MapTechnicalNameConflict(error),
                        StorageProfileRevisionConflict error => MapRevisionConflict(error),
                        StorageProfileTargetMappingsInvalid error => MapTargetMappingsInvalid(error),
                        KubernetesStorageDiscoveryUnavailable error => MapDiscoveryUnavailable(error),
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
