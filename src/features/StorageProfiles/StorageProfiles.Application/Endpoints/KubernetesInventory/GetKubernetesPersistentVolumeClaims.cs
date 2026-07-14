using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesPersistentVolumeClaimsRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>;

public sealed class GetKubernetesPersistentVolumeClaimsHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumeClaimsRequest, IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> HandleAsync(
        GetKubernetesPersistentVolumeClaimsRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumeClaimsAsync(cancellationToken);
}

public static class GetKubernetesPersistentVolumeClaimsEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesPersistentVolumeClaimsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/persistent-volume-claims", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetKubernetesPersistentVolumeClaimsRequest(), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesPersistentVolumeClaims")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>();
        return app;
    }
}
