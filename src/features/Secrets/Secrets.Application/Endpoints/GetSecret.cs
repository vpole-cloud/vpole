using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union GetSecretOutcome(
    SecretMetadata,
    SecretMissing);

public sealed record GetSecretRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetSecretOutcome>, ITenantReadOperation;

public sealed class GetSecretValidator : AbstractValidator<GetSecretRequest>
{
    public GetSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class GetSecretHandler(LoadSecretById load, IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetSecretRequest, GetSecretOutcome>
{
    public async Task<GetSecretOutcome> HandleAsync(
        GetSecretRequest request,
        CancellationToken cancellationToken = default)
    {
        var secret = await load(request.TenantId, request.Id, cancellationToken);
        return secret is null ? new SecretMissing() : SecretMetadata.From(secret) with
        {
            GitOps = await git.StatusAsync(
                new LoadGitResourceStatusRequest(
                    TenantId: request.TenantId,
                    Kind: "Secret",
                    ResourceId: secret.Id),
                ct: cancellationToken)
        };
    }
}

public static class GetSecretEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetSecretRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Ok(metadata),
                    SecretMissing error => MapMissing(error)
                }))
            .WithName("GetSecret");

        return app;
    }
}
