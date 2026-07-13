using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
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
