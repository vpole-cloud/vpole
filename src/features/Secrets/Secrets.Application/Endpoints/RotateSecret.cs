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

public sealed record RotateSecretBody(long ExpectedVersion, Dictionary<string, string> Values)
{
    public override string ToString() => "RotateSecretBody([REDACTED])";
}

public sealed record RotateSecretRequest(
    [FromRoute] string Id, [FromBody] RotateSecretBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SecretMutationOutcome>, ITenantWriteOperation;

public sealed class RotateSecretValidator : AbstractValidator<RotateSecretRequest>
{
    public RotateSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.ExpectedVersion).GreaterThan(0);
            RuleFor(x => x.Body.Values).Must(SecretValuesValidation.Valid).WithMessage("Secret values are invalid or exceed limits.");
        });
    }
}

public sealed class RotateSecretHandler(SecretStore store)
    : IMediatorOutcomeHandler<RotateSecretRequest, SecretMutationOutcome>
{
    public async Task<SecretMutationOutcome> HandleAsync(RotateSecretRequest request, CancellationToken cancellationToken = default) =>
        await store.RotateAsync(request.TenantId, request.Id, request.Body.ExpectedVersion, request.Body.Values, cancellationToken);
}

public static class RotateSecretEndpoint
{
    public static IEndpointRouteBuilder MapRotateSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/versions", async (IMediator mediator, IHttpProblemMapper problems,
            [AsParameters] RotateSecretRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Ok(metadata),
                SecretDeleted => Results.NoContent(),
                SecretError error => problems.Map(error).ToHttpResult()
            })).WithName("RotateSecret");
        return app;
    }
}
