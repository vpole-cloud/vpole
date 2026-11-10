using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Domains.Application.Common.DomainHttpMappings;

namespace Pole.Features.Domains.Application.Endpoints;

public static class DeleteDomainZone
{
    public static IEndpointRouteBuilder MapDeleteDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}",
                async (
                        IMediator mediator, [AsParameters] DeleteDomainZoneRequest request,
                        CancellationToken cancellationToken) =>
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
        DomainZoneNotFound error => MapDomainZoneNotFound(error),
        GeneratedDomainZoneDeletionRejected error => MapGeneratedDomainZoneDeletionRejected(error),
        DomainZoneHasHostnameBindings error => MapDomainZoneHasHostnameBindings(error),
        DomainZoneDeletionConflict error => MapDomainZoneDeletionConflict(error),
        DomainZoneGitOwned error => MapDomainZoneGitOwned(error),
        ManagedDomainZoneReferenceRemovalRejected error => MapManagedDomainZoneReferenceRemovalRejected(error),
    };
}

public sealed record DeleteDomainZoneRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteDomainZoneOutcome>, ITenantWriteOperation;

public union DeleteDomainZoneOutcome(
    ManagedDomainZoneDeleted,
    DomainZoneNotFound,
    GeneratedDomainZoneDeletionRejected,
    DomainZoneHasHostnameBindings,
    DomainZoneDeletionConflict,
    DomainZoneGitOwned,
    ManagedDomainZoneReferenceRemovalRejected);

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
    DeleteManagedDomainZone deleteManagedDomainZone
) : IMediatorOutcomeHandler<DeleteDomainZoneRequest, DeleteDomainZoneOutcome>
{
    public Task<DeleteDomainZoneOutcome> HandleAsync(
        DeleteDomainZoneRequest request,
        CancellationToken cancellationToken = default) => runMongoTransaction(
        async (transaction, transactionCancellationToken) =>
        {
            var result = await deleteManagedDomainZone(
                request.TenantId,
                request.Id,
                transaction.Session,
                transactionCancellationToken);

            return result switch
            {
                ManagedDomainZoneDeleted deleted => transaction.Commit(deleted),
                DomainZoneNotFound error => transaction.Abort(error),
                GeneratedDomainZoneDeletionRejected error => transaction.Abort(error),
                DomainZoneHasHostnameBindings error => transaction.Abort(error),
                DomainZoneDeletionConflict error => transaction.Abort(error),
                DomainZoneGitOwned error => transaction.Abort(error),
                ManagedDomainZoneReferenceRemovalRejected error => transaction.Abort(error),
            };
        },
        cancellationToken);
}
