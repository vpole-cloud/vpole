using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public static class UpdateStorageProfile
{
    public static IEndpointRouteBuilder MapUpdateStorageProfileEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/profiles/{id}", async (
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

public sealed record UpdateStorageProfileRequest(
    [FromRoute] string Id,
    [FromBody] UpsertStorageProfileRequestBody Body)
    : IMediatorRequest<StorageProfileDto>;

public sealed class UpdateStorageProfileHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery)
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

        var bodyErrors = StorageProfileDtoExtensions.ValidateBody(request.Body);
        var validationResult = await CreateStorageProfileHandler.ValidateKnownStorageClassesAsync(
            request.Body,
            storageDiscovery,
            cancellationToken);
        if (!validationResult.TryGetValue(out var storageClassErrors, out var validationFailure))
            return validationFailure;
        foreach (var (key, value) in storageClassErrors)
            bodyErrors[key] = value;

        if (bodyErrors.Count > 0)
            return Failures.ValidationFailure(bodyErrors);

        var domainResult = request.Body.ToDomain(new StorageProfileFactory.StorageProfileCreateOptions(
            existing.Id,
            existing.CreatedAtUtc,
            DateTime.UtcNow));
        if (!domainResult.TryGetValue(out var profile, out var failure))
            return failure;

        return await dbContext.RunInTransactionAsync<StorageProfileDto>(async (session, ct) =>
        {
            if (profile.IsDefault)
            {
                await dbContext.StorageProfiles.UpdateManyAsync(
                    session,
                    Builders<StorageProfile>.Filter.Ne(x => x.Id, profile.Id),
                    Builders<StorageProfile>.Update.Set(x => x.IsDefault, false),
                    cancellationToken: ct);
            }

            await dbContext.StorageProfiles.ReplaceOneAsync(
                session,
                x => x.Id == profile.Id,
                profile,
                cancellationToken: ct);

            return profile.ToDto();
        }, cancellationToken: cancellationToken);
    }
}
