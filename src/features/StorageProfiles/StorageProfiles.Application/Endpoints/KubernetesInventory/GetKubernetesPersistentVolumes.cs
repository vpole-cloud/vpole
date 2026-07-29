using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesPersistentVolumesRequest(
    [FromQuery] string DeploymentTargetId)
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeDto>>;

public sealed class GetKubernetesPersistentVolumesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumesRequest, IReadOnlyCollection<KubernetesPersistentVolumeDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> HandleAsync(
        GetKubernetesPersistentVolumesRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumesAsync(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
}

public sealed class GetKubernetesPersistentVolumesRequestValidator
    : AbstractValidator<GetKubernetesPersistentVolumesRequest>
{
    public GetKubernetesPersistentVolumesRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .Must(id => ObjectId.TryParse(id?.Trim(), out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
}

public static class GetKubernetesPersistentVolumesEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesPersistentVolumesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/persistent-volumes", async (
                IMediator mediator,
                [AsParameters] GetKubernetesPersistentVolumesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesPersistentVolumes")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeDto>>();
        return app;
    }
}
