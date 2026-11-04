using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.ComputeProfiles.Application.Common.ComputeProfileHttpMappings;
using static Pole.Features.ComputeProfiles.Contracts.HttpMapping.ComputeProfileContractHttpMappings;
using static Pole.Features.DeploymentTargets.Contracts.HttpMapping.DeploymentTargetContractHttpMappings;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateComputeProfileRequestBody(
    string DisplayName,
    string Description,
    ComputeResourceRequirementsRequestBody Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceRequestBody> ExtendedResources,
    IReadOnlyCollection<string> AllowedResourceTypes,
    ComputeProfileTenantAccessRequestBody TenantAccess,
    IReadOnlyCollection<ComputeProfileTargetMappingRequestBody> TargetMappings,
    bool IsEnabled,
    bool IsDefault,
    long Revision)
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

public sealed record UpdateComputeProfileRequest(
    [FromRoute(Name = "computeProfileId")] string ComputeProfileId,
    [FromBody] UpdateComputeProfileRequestBody Body)
    : IMediatorRequest<UpdateComputeProfileOutcome>;

public union UpdateComputeProfileOutcome(
    ComputeProfileDto,
    ComputeProfileNotFound,
    ComputeProfileRevisionConflict,
    SelectedComputeProfileTenantsNotFound,
    ComputeProfileTargetMappingsInvalid,
    DefaultComputeProfileCannotBeDemoted,
    DeploymentTargetReferenceChangeRejected);

public sealed class UpdateComputeProfileHandler(
    LoadComputeProfileById loadComputeProfileById,
    ValidateComputeProfileTenantAccess validateTenantAccess,
    ValidateComputeProfileTargetMappings validateTargetMappings,
    PersistUpdatedComputeProfile persistUpdatedComputeProfile,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateComputeProfileRequest, UpdateComputeProfileOutcome>
{
    public async Task<UpdateComputeProfileOutcome> HandleAsync(
        UpdateComputeProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var current = await loadComputeProfileById(
            request.ComputeProfileId,
            cancellationToken);
        if (current is null)
        {
            return new ComputeProfileNotFound(request.ComputeProfileId);
        }
        if (current.Revision != request.Body.Revision)
        {
            return new ComputeProfileRevisionConflict(request.ComputeProfileId);
        }

        var updatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (updatedAtUtc < current.UpdatedAtUtc)
            updatedAtUtc = current.UpdatedAtUtc;
        var replacement = request.Body.Reconfigure(current, updatedAtUtc);
        if (replacement.Revision == current.Revision)
            return current.ToDto();

        var tenantValidation = await validateTenantAccess(
            replacement.TenantAccess,
            cancellationToken);
        if (tenantValidation is SelectedComputeProfileTenantsNotFound tenantError)
        {
            return tenantError;
        }
        var targetValidation = await validateTargetMappings(
            replacement,
            cancellationToken);
        if (targetValidation is ComputeProfileTargetMappingsInvalid targetError)
        {
            return targetError;
        }

        var persisted = await persistUpdatedComputeProfile(
            current,
            replacement,
            cancellationToken);
        return persisted switch
        {
            ComputeProfile profile => profile.ToDto(),
            ComputeProfileRevisionConflict error => error,
            DefaultComputeProfileCannotBeDemoted error => error,
            DeploymentTargetReferenceChangeRejected error => error
        };
    }
}

public sealed class UpdateComputeProfileRequestValidator
    : AbstractValidator<UpdateComputeProfileRequest>
{
    public UpdateComputeProfileRequestValidator()
    {
        RuleFor(request => request.ComputeProfileId)
            .MongoObjectId("Compute profile ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new UpdateComputeProfileRequestBodyValidator());
    }
}

public sealed class UpdateComputeProfileRequestBodyValidator
    : ComputeProfileMutationRequestBodyValidator<UpdateComputeProfileRequestBody>
{
    public UpdateComputeProfileRequestBodyValidator() =>
        RuleFor(body => body.Revision).GreaterThan(0);
}

public static class UpdateComputeProfileEndpoint
{
    public static IEndpointRouteBuilder MapUpdateComputeProfileEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/{computeProfileId}", async (
                IMediator mediator,
                [AsParameters] UpdateComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfileDto profile => Results.Ok(profile),
                    ComputeProfileNotFound error => MapNotFound(error),
                    ComputeProfileRevisionConflict error => MapRevisionConflict(error),
                    SelectedComputeProfileTenantsNotFound error => MapSelectedTenantsNotFound(error),
                    ComputeProfileTargetMappingsInvalid error => MapTargetMappingsInvalid(error),
                    DefaultComputeProfileCannotBeDemoted error => MapDefaultDemotionRejected(error),
                    DeploymentTargetReferenceChangeRejected error => MapReferenceChangeRejected(error)
                });
            })
            .WithName("UpdateComputeProfile")
            .Produces<ComputeProfileDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
