using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;


public sealed record DetachSecretBindingRequest(
    [FromRoute] string Id, [FromRoute] string BindingId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SecretMutationOutcome>, ITenantWriteOperation;

public sealed class DetachSecretBindingValidator : AbstractValidator<DetachSecretBindingRequest>
{
    public DetachSecretBindingValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.BindingId).MongoObjectId("Binding ID");
    }
}

public sealed class DetachSecretBindingHandler(SecretStore store)
    : IMediatorOutcomeHandler<DetachSecretBindingRequest, SecretMutationOutcome>
{
    public async Task<SecretMutationOutcome> HandleAsync(DetachSecretBindingRequest request, CancellationToken cancellationToken = default) =>
        await store.DetachAsync(request.TenantId, request.Id, request.BindingId, cancellationToken);
}

public static class DetachSecretBindingEndpoint
{
    public static IEndpointRouteBuilder MapDetachSecretBindingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}/bindings/{BindingId}", async (IMediator mediator, IHttpProblemMapper problems,
            [AsParameters] DetachSecretBindingRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Ok(metadata),
                SecretDeleted => Results.NoContent(),
                SecretError error => problems.Map(error).ToHttpResult()
            })).WithName("DetachSecretBinding");
        return app;
    }
}
