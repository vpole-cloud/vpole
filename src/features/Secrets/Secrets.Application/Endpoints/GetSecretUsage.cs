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

public union GetSecretUsageOutcome(
    SecretUsage[],
    SecretError);

public sealed record GetSecretUsageRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetSecretUsageOutcome>, ITenantReadOperation;

public sealed class GetSecretUsageValidator : AbstractValidator<GetSecretUsageRequest>
{
    public GetSecretUsageValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class GetSecretUsageHandler(SecretStore store)
    : IMediatorOutcomeHandler<GetSecretUsageRequest, GetSecretUsageOutcome>
{
    public async Task<GetSecretUsageOutcome> HandleAsync(
        GetSecretUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var secret = await store.GetAsync(request.TenantId, request.Id, cancellationToken);
        return secret is null ? new SecretError(new SecretMissing()) : await store.UsageAsync(secret, cancellationToken);
    }
}

public static class GetSecretUsageEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/usage", async (
            IMediator mediator,
            IHttpProblemMapper problems,
            [AsParameters] GetSecretUsageRequest request,
            CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretUsage[] usage => Results.Ok(usage),
                SecretError error => problems.Map(error).ToHttpResult()
            }))
            .WithName("GetSecretUsage");

        return app;
    }
}
