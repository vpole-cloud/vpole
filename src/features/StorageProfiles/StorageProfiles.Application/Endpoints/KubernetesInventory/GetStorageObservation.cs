using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.StorageProfiles.Application.Common.StorageProfileHttpMappings;

namespace Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;

public sealed record GetStorageObservationRequest([FromQuery] string DeploymentTargetId)
    : IMediatorRequest<GetStorageObservationOutcome>;

public union GetStorageObservationOutcome(
    StorageObservationSnapshot,
    KubernetesStorageDiscoveryUnavailable);

public sealed class GetStorageObservationHandler(StorageObservationReader reader)
    : IMediatorOutcomeHandler<GetStorageObservationRequest, GetStorageObservationOutcome>
{
    public async Task<GetStorageObservationOutcome> HandleAsync(GetStorageObservationRequest request, CancellationToken cancellationToken = default) =>
        await reader.ReadAsync(request.DeploymentTargetId.Trim(), cancellationToken) switch
        {
            StorageObservationSnapshot snapshot => snapshot,
            KubernetesStorageDiscoveryUnavailable unavailable => unavailable
        };
}

public sealed class GetStorageObservationRequestValidator : AbstractValidator<GetStorageObservationRequest>
{
    public GetStorageObservationRequestValidator() => RuleFor(request => request.DeploymentTargetId).MongoObjectId("Deployment target ID");
}

public static class GetStorageObservationEndpoint
{
    public static IEndpointRouteBuilder MapGetStorageObservationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/observation", async (IMediator mediator, [AsParameters] GetStorageObservationRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                StorageObservationSnapshot snapshot => Results.Ok(snapshot),
                KubernetesStorageDiscoveryUnavailable unavailable => MapDiscoveryUnavailable(unavailable)
            }))
            .WithName("GetStorageObservation").RequireAuthorization(Policy.Admin)
            .Produces<StorageObservationSnapshot>().ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
