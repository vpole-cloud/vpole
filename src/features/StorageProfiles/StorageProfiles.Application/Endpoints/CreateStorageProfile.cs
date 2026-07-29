using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
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
    : IMediatorRequest<StorageProfileDto>;

public sealed class CreateStorageProfileHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateStorageProfileRequest, StorageProfileDto>
{
    public async Task<Result<StorageProfileDto>> HandleAsync(
        CreateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var mappingValidation =
            await StorageProfileTargetMappingValidation.ValidateKnownStorageClassesAsync(
                request.Body.TargetMappings,
                storageDiscovery,
                cancellationToken);
        if (!mappingValidation.TryGetValue(out var mappingErrors, out var mappingFailure))
            return mappingFailure;
        if (mappingErrors.Count > 0)
            return Failures.ValidationFailure(mappingErrors);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var profile = request.Body.ToDomain(
            new StorageProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now));

        return await dbContext.RunInTransactionAsync<StorageProfileDto>(async (session, ct) =>
        {
            if (profile.IsDefault)
            {
                await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    dbContext,
                    session,
                    exceptProfileId: null,
                    now,
                    ct);
            }

            await dbContext.StorageProfiles.InsertOneAsync(
                session,
                profile,
                cancellationToken: ct);
            return profile.ToDto();
        }, cancellationToken: cancellationToken);
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
                [AsParameters] CreateStorageProfileRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(profile =>
                    Results.Created(
                        $"/api/v1/platform/storage/profiles/{profile.Id}",
                        profile));
            })
            .WithName("CreateStorageProfile")
            .RequireAuthorization(Policy.Admin)
            .Produces<StorageProfileDto>(StatusCodes.Status201Created);
        return app;
    }
}
