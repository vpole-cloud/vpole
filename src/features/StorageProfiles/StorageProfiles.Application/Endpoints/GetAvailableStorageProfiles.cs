using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public static class GetAvailableStorageProfiles
{
    public static IEndpointRouteBuilder MapGetAvailableStorageProfilesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async ([AsParameters] GetAvailableStorageProfilesRequest request, IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetAvailableStorageProfiles")
            .Produces<IReadOnlyCollection<StorageProfileDto>>();

        return app;
    }
}

public sealed record GetAvailableStorageProfilesRequest(
    [FromQuery] string ResourceType,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
    : IMediatorRequest<IReadOnlyCollection<StorageProfileDto>>, ITenantReadOperation;

public sealed class GetAvailableStorageProfilesHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetAvailableStorageProfilesRequest, IReadOnlyCollection<StorageProfileDto>>
{
    public async Task<Result<IReadOnlyCollection<StorageProfileDto>>> HandleAsync(
        GetAvailableStorageProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["TenantId"] = [$"{TenantHeaders.TenantId} is required."]
            });
        }

        if (!Enum.TryParse<ResourceType>(request.ResourceType, ignoreCase: true, out var resourceType))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["ResourceType"] = ["Resource type is invalid."]
            });
        }

        var knownStorageClassNames = await storageDiscovery.GetStorageClassNamesAsync(cancellationToken);
        var profiles = await dbContext.StorageProfiles
            .Find(profile => profile.IsEnabled)
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ToListAsync(cancellationToken);

        return profiles
            .Where(profile => StorageProfileResolver.AllowsTenant(profile, request.TenantId))
            .Where(profile => StorageProfileResolver.AllowsResourceType(profile, resourceType))
            .Where(profile => profile.StorageClassNames.Any(knownStorageClassNames.Contains))
            .Select(profile => profile.ToDto())
            .ToArray();
    }
}
