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

public sealed record GetKubernetesStorageClassesRequest(
    [FromQuery] string DeploymentTargetId)
    : IMediatorRequest<IReadOnlyCollection<KubernetesStorageClassDto>>;

public sealed class GetKubernetesStorageClassesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesStorageClassesRequest, IReadOnlyCollection<KubernetesStorageClassDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> HandleAsync(
        GetKubernetesStorageClassesRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetStorageClassesAsync(
            request.DeploymentTargetId.Trim(),
            cancellationToken);
}

public sealed class GetKubernetesStorageClassesRequestValidator
    : AbstractValidator<GetKubernetesStorageClassesRequest>
{
    public GetKubernetesStorageClassesRequestValidator() =>
        RuleFor(request => request.DeploymentTargetId)
            .Must(id => ObjectId.TryParse(id?.Trim(), out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
}

public static class GetKubernetesStorageClassesEndpoint
{
    public static IEndpointRouteBuilder MapGetKubernetesStorageClassesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/storage-classes", async (
                IMediator mediator,
                [AsParameters] GetKubernetesStorageClassesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetKubernetesStorageClasses")
            .RequireAuthorization(Policy.Admin)
            .Produces<IReadOnlyCollection<KubernetesStorageClassDto>>();
        return app;
    }
}
