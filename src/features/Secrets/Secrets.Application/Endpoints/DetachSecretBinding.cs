using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union DetachSecretBindingOutcome(
    SecretMetadata,
    SecretMissing,
    SecretManagedByProvider,
    SecretGitOwned);

public sealed record DetachSecretBindingRequest(
    [FromRoute] string Id,
    [FromRoute] string BindingId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DetachSecretBindingOutcome>, ITenantWriteOperation;

public sealed class DetachSecretBindingValidator : AbstractValidator<DetachSecretBindingRequest>
{
    public DetachSecretBindingValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.BindingId).MongoObjectId("Binding ID");
    }
}

public sealed class DetachSecretBindingHandler(
    RemoveSecretBinding persist
) : IMediatorOutcomeHandler<DetachSecretBindingRequest, DetachSecretBindingOutcome>
{
    public async Task<DetachSecretBindingOutcome> HandleAsync(
        DetachSecretBindingRequest request,
        CancellationToken cancellationToken = default) => await persist(
            request.TenantId,
            request.Id,
            request.BindingId,
            cancellationToken) switch
        {
            Secret secret => SecretMetadata.From(secret),
            SecretMissing error => error,
            SecretGitOwned error => error,
            SecretManagedByProvider error => error,
        };
}

public static class DetachSecretBindingEndpoint
{
    public static IEndpointRouteBuilder MapDetachSecretBindingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}/bindings/{BindingId}",
                async (
                    IMediator mediator, [AsParameters] DetachSecretBindingRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Ok(metadata),
                    SecretMissing error => MapMissing(error),
                    SecretGitOwned error => MapGitOwned(error),
                    SecretManagedByProvider error => MapManagedByProvider(error),
                }))
            .WithName("DetachSecretBinding");

        return app;
    }
}
