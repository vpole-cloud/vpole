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
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Endpoints;

public sealed record BindSecretBody(
    string ConsumerType,
    string ConsumerId,
    bool AllDeployments,
    string[] DeploymentIds,
    Dictionary<string, string>? KeyMappings = null);

public sealed record BindSecretRequest(
    [FromRoute] string Id,
    [FromBody] BindSecretBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SecretMutationOutcome>, ITenantWriteOperation;

public sealed class BindSecretValidator : AbstractValidator<BindSecretRequest>
{
    public BindSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.ConsumerType).Must(x => ResourceTypeId.TryCreate(x, out _));
            RuleFor(x => x.Body.ConsumerId).MongoObjectId("Consumer ID");
            RuleFor(x => x.Body.DeploymentIds)
                .NotNull()
                .Must(ids => ids is not null &&
                    ids.Length <= 256 &&
                    ids.All(id => ObjectId.TryParse(id, out _)) &&
                    ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length);
            RuleFor(x => x.Body)
                .Must(body => body.DeploymentIds is not null &&
                    (body.AllDeployments ? body.DeploymentIds.Length == 0 : body.DeploymentIds.Length > 0))
                .WithMessage("Select all deployments or a non-empty deployment ID list.");
            RuleFor(x => x.Body.KeyMappings)
                .Must(m => m is null || (m.Count <= 64 &&
                    m.All(x => !string.IsNullOrWhiteSpace(x.Key) &&
                        x.Key.Length <= 128 &&
                        !string.IsNullOrWhiteSpace(x.Value) &&
                        x.Value.Length <= 128) &&
                    m.Values.Distinct().Count() == m.Count));
        });
    }
}

public sealed class BindSecretHandler(SecretStore store)
    : IMediatorOutcomeHandler<BindSecretRequest, SecretMutationOutcome>
{
    public async Task<SecretMutationOutcome> HandleAsync(
        BindSecretRequest request,
        CancellationToken cancellationToken = default) =>
        await store.BindAsync(
            request.TenantId,
            request.Id,
            new SecretBinding(
                ObjectId.GenerateNewId().ToString(),
                request.Body.ConsumerType,
                request.Body.ConsumerId,
                request.Body.AllDeployments,
                request.Body.DeploymentIds,
                request.Body.KeyMappings ?? []),
            cancellationToken);
}

public static class BindSecretEndpoint
{
    public static IEndpointRouteBuilder MapBindSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/bindings", async (
            IMediator mediator,
            IHttpProblemMapper problems,
            [AsParameters] BindSecretRequest request,
            CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretMetadata metadata => Results.Ok(metadata),
                SecretDeleted => Results.NoContent(),
                SecretError error => problems.Map(error).ToHttpResult()
            }))
            .WithName("BindSecret");

        return app;
    }
}
