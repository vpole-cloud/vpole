using FluentValidation;
using Pole.Validation;
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
    : IMediatorRequest<GetKubernetesPersistentVolumesOutcome>;

public union GetKubernetesPersistentVolumesOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeDto>,
    KubernetesStorageDiscoveryUnavailable);

public sealed class GetKubernetesPersistentVolumesHandler(
    DiscoverKubernetesPersistentVolumes discoverPersistentVolumes)
    : IMediatorOutcomeHandler<
        GetKubernetesPersistentVolumesRequest,
        GetKubernetesPersistentVolumesOutcome>
{
    public async Task<GetKubernetesPersistentVolumesOutcome> HandleAsync(
        GetKubernetesPersistentVolumesRequest request,
        CancellationToken cancellationToken = default)
    {
        var discovered = await discoverPersistentVolumes(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
        return discovered switch
        {
            Ok<IReadOnlyCollection<KubernetesPersistentVolumeDto>> persistentVolumes =>
                new GetKubernetesPersistentVolumesOutcome(persistentVolumes.Value),
            Err<KubernetesStorageDiscoveryUnavailable> error =>
                new GetKubernetesPersistentVolumesOutcome(error.Value)
        };
    }
}

public sealed class GetKubernetesPersistentVolumesRequestValidator
    : AbstractValidator<GetKubernetesPersistentVolumesRequest>
{
    public GetKubernetesPersistentVolumesRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
}

public static class GetKubernetesPersistentVolumesEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesPersistentVolumesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/persistent-volumes", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetKubernetesPersistentVolumesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<KubernetesPersistentVolumeDto> persistentVolumes =>
                        Results.Ok(persistentVolumes),
                    KubernetesStorageDiscoveryUnavailable error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetKubernetesPersistentVolumes")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesPersistentVolumeDto>>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
