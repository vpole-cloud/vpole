using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;

public sealed record DeleteSecretRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SecretMutationOutcome>, ITenantWriteOperation;

public sealed class DeleteSecretValidator : AbstractValidator<DeleteSecretRequest>
{
    public DeleteSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class DeleteSecretHandler(SecretStore store)
    : IMediatorOutcomeHandler<DeleteSecretRequest, SecretMutationOutcome>
{
    public async Task<SecretMutationOutcome> HandleAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken = default) =>
        await store.DeleteAsync(request.TenantId, request.Id, cancellationToken);
}

public static class DeleteSecretEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
            IMediator mediator,
            IHttpProblemMapper problems,
            [AsParameters] DeleteSecretRequest request,
            CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Ok(metadata),
                SecretDeleted => Results.NoContent(),
                SecretError error => problems.Map(error).ToHttpResult()
            }))
            .WithName("DeleteSecret");

        return app;
    }
}
