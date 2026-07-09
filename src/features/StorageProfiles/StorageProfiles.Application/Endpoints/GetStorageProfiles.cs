using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public static class GetStorageProfiles
{
    public static IEndpointRouteBuilder MapGetStorageProfilesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/profiles", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetStorageProfilesRequest(), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetStorageProfiles")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<StorageProfileDto>>();

        return app;
    }
}

public sealed record GetStorageProfilesRequest
    : IMediatorRequest<IReadOnlyCollection<StorageProfileDto>>;

public sealed class GetStorageProfilesHandler(IStorageProfileDbContext dbContext)
    : IMediatorHandler<GetStorageProfilesRequest, IReadOnlyCollection<StorageProfileDto>>
{
    public async Task<Pole.ResultType.Result<IReadOnlyCollection<StorageProfileDto>>> HandleAsync(
        GetStorageProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var profiles = await dbContext.StorageProfiles
            .Find(FilterDefinition<StorageProfile>.Empty)
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ToListAsync(cancellationToken);

        return profiles.Select(profile => profile.ToDto()).ToArray();
    }
}
