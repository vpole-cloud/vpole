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

public union GetSecretOutcome(SecretMetadata, SecretError);

public sealed record GetSecretRequest([FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetSecretOutcome>, ITenantReadOperation;

public sealed class GetSecretValidator : AbstractValidator<GetSecretRequest>
{
    public GetSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class GetSecretHandler(SecretStore store) : IMediatorOutcomeHandler<GetSecretRequest, GetSecretOutcome>
{
    public async Task<GetSecretOutcome> HandleAsync(GetSecretRequest request, CancellationToken cancellationToken = default)
    {
        var secret = await store.GetAsync(request.TenantId, request.Id, cancellationToken);
        return secret is null ? new SecretError(new SecretMissing()) : SecretMetadata.From(secret);
    }
}

public static class GetSecretEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (IMediator mediator, IHttpProblemMapper problems,
            [AsParameters] GetSecretRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Ok(metadata),
                SecretError error => problems.Map(error).ToHttpResult()
            })).WithName("GetSecret");
        return app;
    }
}
