// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Mediator;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public static class GetStorageProfiles
{
    public static IEndpointRouteBuilder MapGetStorageProfilesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/profiles",
                async (IMediator mediator, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(new GetStorageProfilesRequest(), cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyCollection<StorageProfileDto> profiles => Results.Ok(profiles),
                    });
                })
            .WithName("GetStorageProfiles")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<StorageProfileDto>>();

        return app;
    }
}

public sealed record GetStorageProfilesRequest : IMediatorRequest<GetStorageProfilesOutcome>;

public union GetStorageProfilesOutcome(IReadOnlyCollection<StorageProfileDto>);

public sealed class GetStorageProfilesHandler(
    LoadStorageProfiles loadStorageProfiles
) : IMediatorOutcomeHandler<GetStorageProfilesRequest, GetStorageProfilesOutcome>
{
    public async Task<GetStorageProfilesOutcome> HandleAsync(
        GetStorageProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var profiles = await loadStorageProfiles(cancellationToken);

        return profiles.Select(profile => profile.ToDto()).ToArray();
    }
}
