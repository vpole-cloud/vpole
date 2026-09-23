using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;

public union ListSecretsOutcome(
    SecretPage);

public sealed record SecretPage(
    SecretMetadata[] Items,
    string? NextCursor);

public sealed record ListSecretsRequest(
    [FromQuery] string? After,
    [FromQuery] int? PageSize,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<ListSecretsOutcome>, ITenantReadOperation;

public sealed class ListSecretsValidator : AbstractValidator<ListSecretsRequest>
{
    public ListSecretsValidator()
    {
        RuleFor(x => x.After).Must(x => x is null || ObjectId.TryParse(x, out _));
        RuleFor(x => x.PageSize).Must(x => x is null or >= 1 and <= 200);
    }
}

public sealed class ListSecretsHandler(LoadSecretPage load)
    : IMediatorOutcomeHandler<ListSecretsRequest, ListSecretsOutcome>
{
    public async Task<ListSecretsOutcome> HandleAsync(
        ListSecretsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await load(request.TenantId, request.After, request.PageSize ?? 50, cancellationToken);
        return new SecretPage(page.Items.Select(SecretMetadata.From).ToArray(), page.NextCursor);
    }
}

public static class ListSecretsEndpoint
{
    public static IEndpointRouteBuilder MapListSecretsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] ListSecretsRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretPage page => Results.Ok(page)
                }))
            .WithName("ListSecrets");

        return app;
    }
}
