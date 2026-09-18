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

public union ListSecretsOutcome(SecretPage, SecretError);

public sealed record ListSecretsRequest(
    [FromQuery] string? After, [FromQuery] int? PageSize,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<ListSecretsOutcome>, ITenantReadOperation;

public sealed class ListSecretsValidator : AbstractValidator<ListSecretsRequest>
{
    public ListSecretsValidator()
    {
        RuleFor(x => x.After).Must(x => x is null || ObjectId.TryParse(x, out _));
        RuleFor(x => x.PageSize).Must(x => x is null or >= 1 and <= 200);

    }
}

public sealed class ListSecretsHandler(SecretStore store) : IMediatorOutcomeHandler<ListSecretsRequest, ListSecretsOutcome>
{
    public async Task<ListSecretsOutcome> HandleAsync(ListSecretsRequest request, CancellationToken cancellationToken = default)
    {
        return await store.ListAsync(request.TenantId, request.After, request.PageSize ?? 50, cancellationToken);
    }
}

public static class ListSecretsEndpoint
{
    public static IEndpointRouteBuilder MapListSecretsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, IHttpProblemMapper problems,
            [AsParameters] ListSecretsRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SecretPage page => Results.Ok(page),
                SecretError error => problems.Map(error).ToHttpResult()
            })).WithName("ListSecrets");
        return app;
    }
}
