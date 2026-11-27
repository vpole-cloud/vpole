// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using static Pole.Features.ComputeProfiles.Application.Common.ComputeProfileHttpMappings;
using static Pole.Features.DeploymentTargets.Contracts.HttpMapping.DeploymentTargetContractHttpMappings;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record CreateComputeProfileRequestBody(
    string TechnicalName,
    string DisplayName,
    string Description,
    ComputeResourceRequirementsRequestBody Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceRequestBody> ExtendedResources,
    IReadOnlyCollection<string> AllowedResourceTypes,
    ComputeProfileTenantAccessRequestBody TenantAccess,
    IReadOnlyCollection<ComputeProfileTargetMappingRequestBody> TargetMappings,
    bool IsEnabled,
    bool IsDefault
) : ComputeProfileMutationRequestBody(
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
    [FromBody] CreateComputeProfileRequestBody Body
) : IMediatorRequest<CreateComputeProfileOutcome>;

public union CreateComputeProfileOutcome(
    ComputeProfileDto,
    ComputeProfileTechnicalNameConflict,
    ComputeProfileRevisionConflict,
    SelectedComputeProfileTenantsNotFound,
    ComputeProfileTargetMappingsInvalid,
    DeploymentTargetReferenceChangeRejected);

public sealed class CreateComputeProfileHandler(
    ValidateComputeProfileTenantAccess validateTenantAccess,
    ValidateComputeProfileTargetMappings validateTargetMappings,
    PersistCreatedComputeProfile persistCreatedComputeProfile,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<CreateComputeProfileRequest, CreateComputeProfileOutcome>
{
    public async Task<CreateComputeProfileOutcome> HandleAsync(
        CreateComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = request.Body.ToDomain(
            request.Body.TechnicalName,
            new ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        var tenantValidation = await validateTenantAccess(profile.TenantAccess, cancellationToken);
        if (tenantValidation is SelectedComputeProfileTenantsNotFound tenantError)
            return tenantError;
        var targetValidation = await validateTargetMappings(profile, cancellationToken);
        if (targetValidation is ComputeProfileTargetMappingsInvalid targetError)
            return targetError;

        var persisted = await persistCreatedComputeProfile(profile, cancellationToken);
        return persisted switch
        {
            ComputeProfile created => created.ToDto(),
            ComputeProfileTechnicalNameConflict conflict => conflict,
            ComputeProfileRevisionConflict conflict => conflict,
            DeploymentTargetReferenceChangeRejected error => error,
        };
    }
}

public sealed class CreateComputeProfileRequestValidator : AbstractValidator<CreateComputeProfileRequest>
{
    public CreateComputeProfileRequestValidator() => RuleFor(request => request.Body)
        .NotNull()
        .SetValidator(new CreateComputeProfileRequestBodyValidator());
}

public sealed class CreateComputeProfileRequestBodyValidator
    : ComputeProfileMutationRequestBodyValidator<CreateComputeProfileRequestBody>
{
    public CreateComputeProfileRequestBodyValidator() =>
        RuleFor(body => body.TechnicalName).Must(ResourceTechnicalName.IsValid);
}

public static class CreateComputeProfileEndpoint
{
    public static IEndpointRouteBuilder MapCreateComputeProfileEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateComputeProfileRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        ComputeProfileDto profile => Results.Created(
                            $"/api/v1/platform/compute-profiles/{profile.Id}",
                            profile),
                        SelectedComputeProfileTenantsNotFound error => MapSelectedTenantsNotFound(error),
                        ComputeProfileTargetMappingsInvalid error => MapTargetMappingsInvalid(error),
                        ComputeProfileRevisionConflict conflict => MapRevisionConflict(conflict),
                        ComputeProfileTechnicalNameConflict conflict => MapNameConflict(conflict),
                        DeploymentTargetReferenceChangeRejected error => MapReferenceChangeRejected(error),
                    });
                })
            .WithName("CreateComputeProfile")
            .Produces<ComputeProfileDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
