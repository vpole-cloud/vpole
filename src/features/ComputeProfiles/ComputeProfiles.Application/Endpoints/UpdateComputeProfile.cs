using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

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

public union UpdateComputeProfileError(
    ComputeProfileNotFound,
    ComputeProfileRevisionConflict,
    ComputeProfileMutationValidationError,
    PersistUpdatedComputeProfileError);

public union UpdateComputeProfileOutcome(
    ComputeProfileDto,
    UpdateComputeProfileError);

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
            return new UpdateComputeProfileError(
                new ComputeProfileNotFound(request.ComputeProfileId));
        }
        if (current.Revision != request.Body.Revision)
        {
            return new UpdateComputeProfileError(
                new ComputeProfileRevisionConflict(request.ComputeProfileId));
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
        if (tenantValidation is Err<SelectedComputeProfileTenantsNotFound> tenantError)
        {
            return new UpdateComputeProfileError(
                new ComputeProfileMutationValidationError(tenantError.Value));
        }
        var targetValidation = await validateTargetMappings(
            replacement,
            cancellationToken);
        if (targetValidation is Err<ComputeProfileTargetMappingsInvalid> targetError)
        {
            return new UpdateComputeProfileError(
                new ComputeProfileMutationValidationError(targetError.Value));
        }

        var persisted = await persistUpdatedComputeProfile(
            current,
            replacement,
            cancellationToken);
        return persisted.Match(
            UpdateComputeProfileOutcome (updated) => updated.ToDto(),
            UpdateComputeProfileOutcome (error) => new UpdateComputeProfileError(error));
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
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateComputeProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ComputeProfileDto profile => Results.Ok(profile),
                    UpdateComputeProfileError error => httpProblems.Map(error).ToHttpResult()
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
