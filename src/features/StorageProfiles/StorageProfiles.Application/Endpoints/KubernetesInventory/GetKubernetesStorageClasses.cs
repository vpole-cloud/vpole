using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesStorageClassesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesStorageClassDto>>;

public sealed class GetKubernetesStorageClassesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesStorageClassesRequest, IReadOnlyCollection<KubernetesStorageClassDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> HandleAsync(
        GetKubernetesStorageClassesRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetStorageClassesAsync(cancellationToken);
}

public static class GetKubernetesStorageClassesEndpoint
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
}
