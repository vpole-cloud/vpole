using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;

public union DetachSecretBindingOutcome(
    SecretMetadata,
    SecretMissing,
    SecretManagedByProvider);

public sealed record DetachSecretBindingRequest(
    [FromRoute] string Id,
    [FromRoute] string BindingId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DetachSecretBindingOutcome>, ITenantWriteOperation;

public sealed class DetachSecretBindingValidator : AbstractValidator<DetachSecretBindingRequest>
{
    public DetachSecretBindingValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.BindingId).MongoObjectId("Binding ID");
    }
}

public sealed class DetachSecretBindingHandler(RemoveSecretBinding persist)
    : IMediatorOutcomeHandler<DetachSecretBindingRequest, DetachSecretBindingOutcome>
{
    public async Task<DetachSecretBindingOutcome> HandleAsync(
        DetachSecretBindingRequest request,
        CancellationToken cancellationToken = default) =>
        (await persist(request.TenantId, request.Id, request.BindingId, cancellationToken)) switch
        {
            Secret secret => SecretMetadata.From(secret),
            SecretMissing error => error,
            SecretManagedByProvider error => error,
        };
}

public static class DetachSecretBindingEndpoint
{
    public static IEndpointRouteBuilder MapDetachSecretBindingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}/bindings/{BindingId}", async (
                IMediator mediator,
                IHttpProblemMapper problems,
                [AsParameters] DetachSecretBindingRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Ok(metadata),
                    SecretMissing error => problems.Map(error).ToHttpResult(),
                    SecretManagedByProvider error => problems.Map(error).ToHttpResult()
                }))
            .WithName("DetachSecretBinding");

        return app;
    }
}
