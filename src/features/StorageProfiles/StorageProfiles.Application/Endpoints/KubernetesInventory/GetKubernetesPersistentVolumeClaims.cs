using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.StorageProfiles.Application.Common.StorageProfileHttpMappings;

namespace Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;

public sealed record GetKubernetesPersistentVolumeClaimsRequest(
    [FromQuery] string DeploymentTargetId)
    : IMediatorRequest<GetKubernetesPersistentVolumeClaimsOutcome>;

public union GetKubernetesPersistentVolumeClaimsOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
    KubernetesStorageDiscoveryUnavailable);

public sealed class GetKubernetesPersistentVolumeClaimsHandler(
    DiscoverKubernetesPersistentVolumeClaims discoverPersistentVolumeClaims)
    : IMediatorOutcomeHandler<
        GetKubernetesPersistentVolumeClaimsRequest,
        GetKubernetesPersistentVolumeClaimsOutcome>
{
    public async Task<GetKubernetesPersistentVolumeClaimsOutcome> HandleAsync(
        GetKubernetesPersistentVolumeClaimsRequest request,
        CancellationToken cancellationToken = default)
    {
        var discovered = await discoverPersistentVolumeClaims(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
        return discovered switch
        {
            IReadOnlyCollection<KubernetesPersistentVolumeClaimDto> persistentVolumeClaims =>
                persistentVolumeClaims,
            KubernetesStorageDiscoveryUnavailable error =>
                error
        };
    }
}

public sealed class GetKubernetesPersistentVolumeClaimsRequestValidator
    : AbstractValidator<GetKubernetesPersistentVolumeClaimsRequest>
{
    public GetKubernetesPersistentVolumeClaimsRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<KubernetesPersistentVolumeClaimDto> persistentVolumeClaims =>
                        Results.Ok(persistentVolumeClaims),
                    KubernetesStorageDiscoveryUnavailable error =>
                        MapDiscoveryUnavailable(error)
                });
            })
            .WithName("GetKubernetesPersistentVolumeClaims")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
