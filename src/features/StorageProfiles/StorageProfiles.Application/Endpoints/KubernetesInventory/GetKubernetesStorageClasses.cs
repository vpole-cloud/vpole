using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;

public sealed record GetKubernetesStorageClassesRequest(
    [FromQuery] string DeploymentTargetId)
    : IMediatorRequest<GetKubernetesStorageClassesOutcome>;

public union GetKubernetesStorageClassesOutcome(
    IReadOnlyCollection<KubernetesStorageClassDto>,
    KubernetesStorageDiscoveryUnavailable);

public sealed class GetKubernetesStorageClassesHandler(
    DiscoverKubernetesStorageClasses discoverStorageClasses)
    : IMediatorOutcomeHandler<
        GetKubernetesStorageClassesRequest,
        GetKubernetesStorageClassesOutcome>
{
    public async Task<GetKubernetesStorageClassesOutcome> HandleAsync(
        GetKubernetesStorageClassesRequest request,
        CancellationToken cancellationToken = default)
    {
        var discovered = await discoverStorageClasses(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
        return discovered switch
        {
            Ok<IReadOnlyCollection<KubernetesStorageClassDto>> storageClasses =>
                new GetKubernetesStorageClassesOutcome(storageClasses.Value),
            Err<KubernetesStorageDiscoveryUnavailable> error =>
                new GetKubernetesStorageClassesOutcome(error.Value)
        };
    }
}

public sealed class GetKubernetesStorageClassesRequestValidator
    : AbstractValidator<GetKubernetesStorageClassesRequest>
{
    public GetKubernetesStorageClassesRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
}

public static class GetKubernetesStorageClassesEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesStorageClassesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/storage-classes", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetKubernetesStorageClassesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<KubernetesStorageClassDto> storageClasses =>
                        Results.Ok(storageClasses),
                    KubernetesStorageDiscoveryUnavailable error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetKubernetesStorageClasses")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesStorageClassDto>>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
