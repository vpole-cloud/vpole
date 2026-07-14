using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesPersistentVolumesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeDto>>;

public sealed class GetKubernetesPersistentVolumesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumesRequest, IReadOnlyCollection<KubernetesPersistentVolumeDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> HandleAsync(
        GetKubernetesPersistentVolumesRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumesAsync(cancellationToken);
}

public static class GetKubernetesPersistentVolumesEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesPersistentVolumesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/persistent-volumes", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetKubernetesPersistentVolumesRequest(), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesPersistentVolumes")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeDto>>();
        return app;
    }
}
