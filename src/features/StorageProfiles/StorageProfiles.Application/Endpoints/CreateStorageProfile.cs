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

public static class CreateStorageProfile
{
    public static IEndpointRouteBuilder MapCreateStorageProfileEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/profiles", async (
                IMediator mediator,
                [FromBody] UpsertStorageProfileRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new CreateStorageProfileRequest(body), cancellationToken);
                return result.ToHttpResult(profile => Results.Created($"/api/v1/platform/storage/profiles/{profile.Id}", profile));
            })
            .WithName("CreateStorageProfile")
            .RequireAuthorization(Policy.Admin)
            .Produces<StorageProfileDto>(StatusCodes.Status201Created);

        return app;
    }
}

public sealed record CreateStorageProfileRequest(
    UpsertStorageProfileRequestBody Body)
    : IMediatorRequest<StorageProfileDto>;

public sealed class CreateStorageProfileHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<CreateStorageProfileRequest, StorageProfileDto>
{
    public async Task<Result<StorageProfileDto>> HandleAsync(
        CreateStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var bodyErrors = StorageProfileDtoExtensions.ValidateBody(request.Body);
        var storageClassErrors = await ValidateKnownStorageClassesAsync(request.Body, storageDiscovery, cancellationToken);
        foreach (var (key, value) in storageClassErrors)
            bodyErrors[key] = value;

        if (bodyErrors.Count > 0)
            return Failures.ValidationFailure(bodyErrors);

        var domainResult = request.Body.ToDomain(StorageProfileFactory.DefaultCreateOptions);
        if (!domainResult.TryGetValue(out var profile, out var failure))
            return failure;

        return await dbContext.RunInTransactionAsync<StorageProfileDto>(async (session, ct) =>
        {
            if (profile.IsDefault)
            {
                await dbContext.StorageProfiles.UpdateManyAsync(
                    session,
                    Builders<StorageProfile>.Filter.Empty,
                    Builders<StorageProfile>.Update.Set(x => x.IsDefault, false),
                    cancellationToken: ct);
            }

            await dbContext.StorageProfiles.InsertOneAsync(session, profile, cancellationToken: ct);

            return profile.ToDto();
        }, cancellationToken: cancellationToken);
    }

    internal static async Task<Dictionary<string, string[]>> ValidateKnownStorageClassesAsync(
        UpsertStorageProfileRequestBody? body,
        IStorageDiscoveryService storageDiscovery,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null)
            return errors;

        var knownStorageClassNames = await storageDiscovery.GetStorageClassNamesAsync(cancellationToken);
        var requestedStorageClassNames = StorageProfileFactory.NormalizeStorageClassNames(body.StorageClassNames ?? []);
        var unknownStorageClassNames = requestedStorageClassNames
            .Where(name => !knownStorageClassNames.Contains(name))
            .ToArray();

        if (unknownStorageClassNames.Length > 0)
        {
            errors["Body.StorageClassNames"] =
            [
                $"Unknown Kubernetes storage classes: {string.Join(", ", unknownStorageClassNames)}."
            ];
        }

        return errors;
    }
}
