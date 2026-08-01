using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.ResultType;

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
    : IMediatorRequest<StorageProfileDto>;

public sealed class UpdateStorageProfileHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateStorageProfileRequest, StorageProfileDto>
{
    public async Task<Result<StorageProfileDto>> HandleAsync(
        UpdateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.StorageProfiles
            .Find(profile => profile.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return Failures.NotFound("Storage profile not found.", request.Id);
        if (existing.Revision != request.Body.Revision)
            return Failures.Conflict("The storage profile changed. Reload it and retry.");

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
        if (now < existing.UpdatedAtUtc)
            now = existing.UpdatedAtUtc;
        var updated = request.Body.Reconfigure(existing, now);
        if (updated.Revision == existing.Revision)
            return existing.ToDto();

        return await dbContext.RunInTransactionAsync<StorageProfileDto>(async (session, ct) =>
        {
            if (updated.IsDefault)
            {
                await StorageProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    dbContext,
                    session,
                    updated.Id,
                    now,
                    ct);
            }

            var replacement = await dbContext.StorageProfiles.ReplaceOneAsync(
                session,
                Builders<StorageProfile>.Filter.And(
                    Builders<StorageProfile>.Filter.Eq(
                        profile => profile.Id,
                        existing.Id),
                    Builders<StorageProfile>.Filter.Eq(
                        profile => profile.Revision,
                        existing.Revision)),
                updated,
                cancellationToken: ct);
            return replacement.ModifiedCount == 1
                ? updated.ToDto()
                : Failures.Conflict("The storage profile changed. Reload it and retry.");
        }, cancellationToken: cancellationToken);
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
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateStorageProfile")
            .RequireAuthorization(Policy.Admin)
            .Produces<StorageProfileDto>();
        return app;
    }
}
