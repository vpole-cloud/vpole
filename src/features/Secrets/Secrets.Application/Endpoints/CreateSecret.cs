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

public sealed record CreateSecretBody(string ResourceGroupId, string IsolationScopeId,
    string Name, string Purpose, Dictionary<string, string> Values)
{
    public override string ToString() => "CreateSecretBody([REDACTED])";
}

public sealed record CreateSecretRequest(
    [FromBody] CreateSecretBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SecretMutationOutcome>, ITenantCreateOperation;

public sealed class CreateSecretValidator : AbstractValidator<CreateSecretRequest>
{
    public CreateSecretValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.ResourceGroupId).MongoObjectId("Resource group ID");
            RuleFor(x => x.Body.IsolationScopeId).MongoObjectId("Isolation scope ID");
            RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Body.Purpose).NotNull().MaximumLength(500);
            RuleFor(x => x.Body.Values).Must(SecretValuesValidation.Valid).WithMessage("Secret values are invalid or exceed limits.");
        });
    }
}

public sealed class CreateSecretHandler(SecretStore store)
    : IMediatorOutcomeHandler<CreateSecretRequest, SecretMutationOutcome>
{
    public async Task<SecretMutationOutcome> HandleAsync(CreateSecretRequest request, CancellationToken cancellationToken = default) =>
        await store.CreateAsync(request.TenantId, request.Body.ResourceGroupId, request.Body.IsolationScopeId, request.Body.Name, request.Body.Purpose, request.Body.Values, cancellationToken);
}

public static class CreateSecretEndpoint
{
    public static IEndpointRouteBuilder MapCreateSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, IHttpProblemMapper problems,
            [AsParameters] CreateSecretRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Created($"/api/v1/secrets/{metadata.Id}", metadata),
                SecretDeleted => Results.NoContent(),
                SecretError error => problems.Map(error).ToHttpResult()
            })).WithName("CreateSecret");
        return app;
    }
}
