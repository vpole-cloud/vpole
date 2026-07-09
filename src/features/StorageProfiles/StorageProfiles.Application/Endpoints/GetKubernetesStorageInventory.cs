using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public static class GetKubernetesStorageInventory
{
    public static IEndpointRouteBuilder MapGetKubernetesStorageClassesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/storage-classes", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetKubernetesStorageClassesRequest(), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesStorageClasses")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesStorageClassDto>>();

        return app;
    }

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

public sealed record GetKubernetesStorageClassesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesStorageClassDto>>;

public sealed record GetKubernetesPersistentVolumesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeDto>>;

public sealed record GetKubernetesPersistentVolumeClaimsRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>;

public sealed class GetKubernetesStorageClassesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesStorageClassesRequest, IReadOnlyCollection<KubernetesStorageClassDto>>
{
    public async Task<Pole.ResultType.Result<IReadOnlyCollection<KubernetesStorageClassDto>>> HandleAsync(
        GetKubernetesStorageClassesRequest request,
        CancellationToken cancellationToken = default) =>
        (await storageDiscovery.GetStorageClassesAsync(cancellationToken)).ToArray();
}

public sealed class GetKubernetesPersistentVolumesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumesRequest, IReadOnlyCollection<KubernetesPersistentVolumeDto>>
{
    public async Task<Pole.ResultType.Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> HandleAsync(
        GetKubernetesPersistentVolumesRequest request,
        CancellationToken cancellationToken = default) =>
        (await storageDiscovery.GetPersistentVolumesAsync(cancellationToken)).ToArray();
}

public sealed class GetKubernetesPersistentVolumeClaimsHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumeClaimsRequest, IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>
{
    public async Task<Pole.ResultType.Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> HandleAsync(
        GetKubernetesPersistentVolumeClaimsRequest request,
        CancellationToken cancellationToken = default) =>
        (await storageDiscovery.GetPersistentVolumeClaimsAsync(cancellationToken)).ToArray();
}
