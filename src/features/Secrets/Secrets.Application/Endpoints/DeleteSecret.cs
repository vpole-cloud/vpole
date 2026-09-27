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
using Pole.ResultType;
using Pole.Validation;

using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public sealed record SecretDeleted;

public union DeleteSecretOutcome(
    SecretDeleted,
    SecretMissing,
    SecretBindingsRemain,
    SecretReferenceRejected);

public sealed record DeleteSecretRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteSecretOutcome>, ITenantWriteOperation;

public sealed class DeleteSecretValidator : AbstractValidator<DeleteSecretRequest>
{
    public DeleteSecretValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class DeleteSecretHandler(DeleteStoredSecret persist)
    : IMediatorOutcomeHandler<DeleteSecretRequest, DeleteSecretOutcome>
{
    public async Task<DeleteSecretOutcome> HandleAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken = default) =>
        (await persist(request.TenantId, request.Id, cancellationToken)) switch
        {
            Unit => new SecretDeleted(),
            SecretMissing error => error,
            SecretBindingsRemain error => error,
            SecretReferenceRejected error => error,
        };
}

public static class DeleteSecretEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSecretEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteSecretRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretDeleted => Results.NoContent(),
                    SecretMissing error => MapMissing(error),
                    SecretBindingsRemain error => MapBindingsRemain(error),
                    SecretReferenceRejected error => MapReferenceRejected(error)
                }))
            .WithName("DeleteSecret");

        return app;
    }
}
