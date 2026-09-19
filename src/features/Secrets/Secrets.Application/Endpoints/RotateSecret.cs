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

public union RotateSecretOutcome(
    SecretMetadata,
    SecretMissing,
    SecretVersionConflict,
    SecretMappedKeysRequired,
    SecretEncryptionFailed);

public sealed record RotateSecretBody(
    long ExpectedVersion,
    Dictionary<string, string> Values)
{
    public override string ToString() => "RotateSecretBody([REDACTED])";
}

public sealed record RotateSecretRequest(
    [FromRoute] string Id,
    [FromBody] RotateSecretBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RotateSecretOutcome>, ITenantWriteOperation;

public sealed class RotateSecretValidator : AbstractValidator<RotateSecretRequest>
{
    public RotateSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.ExpectedVersion).GreaterThan(0);
            RuleFor(x => x.Body.Values)
                .Must(SecretValuesValidation.Valid)
                .WithMessage("Secret values are invalid or exceed limits.");
        });
    }
}

public sealed class RotateSecretHandler(PersistRotatedSecret persist)
    : IMediatorOutcomeHandler<RotateSecretRequest, RotateSecretOutcome>
{
    public async Task<RotateSecretOutcome> HandleAsync(
        RotateSecretRequest request,
        CancellationToken cancellationToken = default) =>
        (await persist(
            request.TenantId,
            request.Id,
            request.Body.ExpectedVersion,
            request.Body.Values,
            cancellationToken)) switch
        {
            Secret secret => SecretMetadata.From(secret),
            SecretMissing error => error,
            SecretVersionConflict error => error,
            SecretMappedKeysRequired error => error,
            SecretEncryptionFailed error => error,
        };
}

public static class RotateSecretEndpoint
{
    public static IEndpointRouteBuilder MapRotateSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/versions", async (
                IMediator mediator,
                IHttpProblemMapper problems,
                [AsParameters] RotateSecretRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Ok(metadata),
                    SecretMissing error => problems.Map(error).ToHttpResult(),
                    SecretVersionConflict error => problems.Map(error).ToHttpResult(),
                    SecretMappedKeysRequired error => problems.Map(error).ToHttpResult(),
                    SecretEncryptionFailed error => problems.Map(error).ToHttpResult()
                }))
            .WithName("RotateSecret");

        return app;
    }
}
