using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Application.Persistence;
using Pole.Database;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Domains.Application.Endpoints;

public static class DeleteDomainZone
{
    public static IEndpointRouteBuilder MapDeleteDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteDomainZoneRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(ToHttpResult))
            .WithName("DeleteDomainZone")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static IResult ToHttpResult(DeleteDomainZoneOutcome outcome) => outcome switch
    {
        ManagedDomainZoneDeleted => Results.NoContent(),
        DeleteManagedDomainZoneError error => ToProblem(error)
    };

    private static IResult ToProblem(DeleteManagedDomainZoneError error) => error switch
    {
        DomainZoneNotFound notFound => Problem(
            StatusCodes.Status404NotFound,
            "domain-zone-not-found",
            "Domain zone not found",
            "domains.zone.not-found",
            new Dictionary<string, object?>
            {
                ["domainZoneId"] = notFound.ZoneId
            }),
        GeneratedDomainZoneDeletionRejected generated => Problem(
            StatusCodes.Status409Conflict,
            "domain-zone-generated",
            "Generated domain zone cannot be deleted",
            "domains.zone.delete.generated",
            new Dictionary<string, object?>
            {
                ["domainZoneId"] = generated.ZoneId
            }),
        DomainZoneHasHostnameBindings referenced => Problem(
            StatusCodes.Status409Conflict,
            "domain-zone-has-hostname-bindings",
            "Domain zone still has hostname bindings",
            "domains.zone.delete.has-hostname-bindings",
            new Dictionary<string, object?>
            {
                ["domainZoneId"] = referenced.ZoneId,
                ["bindingCount"] = referenced.BindingCount
            }),
        DomainZoneDeletionConflict conflict => Problem(
            StatusCodes.Status409Conflict,
            "domain-zone-deletion-conflict",
            "Domain zone changed during deletion",
            "domains.zone.delete.conflict",
            new Dictionary<string, object?>
            {
                ["domainZoneId"] = conflict.ZoneId
            }),
        ManagedDomainZoneReferenceRemovalRejected rejected => Problem(
            StatusCodes.Status409Conflict,
            "domain-zone-reference-removal-rejected",
            "Deployment target rejected domain zone deletion",
            "domains.zone.delete.reference-removal-rejected",
            new Dictionary<string, object?>
            {
                ["domainZoneId"] = rejected.ZoneId,
                ["deploymentTargetId"] = rejected.DeploymentTargetId
            })
    };

    private static IResult Problem(
        int status,
        string type,
        string title,
        string code,
        IDictionary<string, object?> extensions)
    {
        extensions["code"] = code;
        return Results.Problem(
            statusCode: status,
            title: title,
            type: $"https://pole.dev/problems/{type}",
            extensions: extensions);
    }
}

public sealed record DeleteDomainZoneRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteDomainZoneOutcome>, ITenantWriteOperation;

public union DeleteDomainZoneOutcome(
    ManagedDomainZoneDeleted,
    DeleteManagedDomainZoneError);

public sealed class DeleteDomainZoneRequestValidator : AbstractValidator<DeleteDomainZoneRequest>
{
    public DeleteDomainZoneRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class DeleteDomainZoneHandler(
    RunMongoOutcomeTransaction<DeleteDomainZoneOutcome> runMongoTransaction,
    DeleteManagedDomainZone deleteManagedDomainZone)
    : IMediatorOutcomeHandler<DeleteDomainZoneRequest, DeleteDomainZoneOutcome>
{
    public Task<DeleteDomainZoneOutcome> HandleAsync(
        DeleteDomainZoneRequest request,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(
            async (transaction, transactionCancellationToken) =>
            {
                var result = await deleteManagedDomainZone(
                    request.TenantId,
                    request.Id,
                    transaction.Session,
                    transactionCancellationToken);

                return result.Match(
                    deleted => transaction.Commit(deleted),
                    error => transaction.Abort(error));
            },
            cancellationToken);
}
