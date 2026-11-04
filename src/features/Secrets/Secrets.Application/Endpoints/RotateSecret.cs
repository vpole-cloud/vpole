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

using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union RotateSecretOutcome(
    SecretMetadata,
    SecretMissing,
    SecretVersionConflict,
    SecretMappedKeysRequired,
    SecretEncryptionFailed,
    SecretManagedByProvider);

public sealed record RotateSecretBody(
    long ExpectedVersion,
    Dictionary<string, string> Values)
{
    public override string ToString() => "RotateSecretBody([REDACTED])";
}

public sealed record RotateSecretRequest(
    [FromRoute] string Id,
    [FromBody] RotateSecretBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<RotateSecretOutcome>, ITenantWriteOperation;

public sealed class RotateSecretValidator : AbstractValidator<RotateSecretRequest>
{
    public RotateSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.ExpectedVersion).GreaterThanOrEqualTo(0);
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
            SecretManagedByProvider error => error,
        };
}

public static class RotateSecretEndpoint
{
    public static IEndpointRouteBuilder MapRotateSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/versions", async (
                IMediator mediator,
                [AsParameters] RotateSecretRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Ok(metadata),
                    SecretMissing error => MapMissing(error),
                    SecretVersionConflict error => MapVersionConflict(error),
                    SecretMappedKeysRequired error => MapMappedKeysRequired(error),
                    SecretEncryptionFailed error => MapEncryptionFailed(error),
                    SecretManagedByProvider error => MapManagedByProvider(error)
                }))
            .WithName("RotateSecret");

        return app;
    }
}
