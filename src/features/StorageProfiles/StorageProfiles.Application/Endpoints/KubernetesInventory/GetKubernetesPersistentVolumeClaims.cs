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

public sealed record GetKubernetesPersistentVolumeClaimsRequest(
    [FromQuery] string DeploymentTargetId)
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>;

public sealed class GetKubernetesPersistentVolumeClaimsHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumeClaimsRequest, IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> HandleAsync(
        GetKubernetesPersistentVolumeClaimsRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumeClaimsAsync(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
}

public sealed class GetKubernetesPersistentVolumeClaimsRequestValidator
    : AbstractValidator<GetKubernetesPersistentVolumeClaimsRequest>
{
    public GetKubernetesPersistentVolumeClaimsRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .Must(id => ObjectId.TryParse(id?.Trim(), out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
}

public static class GetKubernetesPersistentVolumeClaimsEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesPersistentVolumeClaimsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/persistent-volume-claims", async (
                IMediator mediator,
                [AsParameters] GetKubernetesPersistentVolumeClaimsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesPersistentVolumeClaims")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>();
        return app;
    }
}
