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
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Domains.Application.Endpoints;

public static class DeleteDomainZone
{
    public static IEndpointRouteBuilder MapDeleteDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteDomainZoneRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    outcome => ToHttpResult(outcome, httpProblems)))
            .WithName("DeleteDomainZone")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static IResult ToHttpResult(
        DeleteDomainZoneOutcome outcome,
        IHttpProblemMapper httpProblems) => outcome switch
        {
            ManagedDomainZoneDeleted => Results.NoContent(),
            DeleteManagedDomainZoneError error =>
                httpProblems.Map(error).ToHttpResult()
        };
}

public sealed record DeleteDomainZoneRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
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
