using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union CreateSecretOutcome(
    SecretMetadata,
    SecretNameAlreadyExists,
    SecretReferenceRejected,
    SecretEncryptionFailed);

public sealed record CreateSecretBody(
    string ResourceGroupId,
    string IsolationScopeId,
    string Name,
    string Purpose,
    Dictionary<string, string> Values
)
{
    public override string ToString() => "CreateSecretBody([REDACTED])";
}

public sealed record CreateSecretRequest(
    [FromBody] CreateSecretBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateSecretOutcome>, ITenantCreateOperation;

public sealed class CreateSecretValidator : AbstractValidator<CreateSecretRequest>
{
    public CreateSecretValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(
            x => x.Body is not null,
            () =>
            {
                RuleFor(x => x.Body.ResourceGroupId).MongoObjectId("Resource group ID");
                RuleFor(x => x.Body.IsolationScopeId).MongoObjectId("Isolation scope ID");
                RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(200);
                RuleFor(x => x.Body.Purpose).NotNull().MaximumLength(500);
                RuleFor(x => x.Body.Values)
                    .Must(SecretValuesValidation.Valid)
                    .WithMessage("Secret values are invalid or exceed limits.");
            });
    }
}

public sealed class CreateSecretHandler(
    PersistCreatedSecret persist
) : IMediatorOutcomeHandler<CreateSecretRequest, CreateSecretOutcome>
{
    public async Task<CreateSecretOutcome> HandleAsync(
        CreateSecretRequest request,
        CancellationToken cancellationToken = default) => await persist(
            request.TenantId,
            request.Body.ResourceGroupId,
            request.Body.IsolationScopeId,
            request.Body.Name,
            request.Body.Purpose,
            request.Body.Values,
            cancellationToken) switch
        {
            Secret secret => SecretMetadata.From(secret),
            SecretNameAlreadyExists error => error,
            SecretReferenceRejected error => error,
            SecretEncryptionFailed error => error,
        };
}

public static class CreateSecretEndpoint
{
    public static IEndpointRouteBuilder MapCreateSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateSecretRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SecretMetadata metadata => Results.Created($"/api/v1/secrets/{metadata.Id}", metadata),
                    SecretNameAlreadyExists error => MapNameAlreadyExists(error),
                    SecretReferenceRejected error => MapReferenceRejected(error),
                    SecretEncryptionFailed error => MapEncryptionFailed(error),
                }))
            .WithName("CreateSecret");

        return app;
    }
}
