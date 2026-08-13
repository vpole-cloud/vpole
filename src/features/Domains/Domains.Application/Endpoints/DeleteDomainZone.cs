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
                (await mediator.Send(request, cancellationToken)).ToHttpResult(_ => Results.NoContent()))
            .WithName("DeleteDomainZone")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}

public sealed record DeleteDomainZoneRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteDomainZoneResponse>, ITenantWriteOperation;

public sealed record DeleteDomainZoneResponse(string Id);

public sealed class DeleteDomainZoneRequestValidator : AbstractValidator<DeleteDomainZoneRequest>
{
    public DeleteDomainZoneRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class DeleteDomainZoneHandler(
    RunMongoTransaction<DeleteDomainZoneResponse> runMongoTransaction,
    DeleteManagedDomainZone deleteManagedDomainZone)
    : IMediatorHandler<DeleteDomainZoneRequest, DeleteDomainZoneResponse>
{
    public async Task<Result<DeleteDomainZoneResponse>> HandleAsync(
        DeleteDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        return await runMongoTransaction(async (transaction, transactionCancellationToken) =>
        {
            var deleted = await deleteManagedDomainZone(
                request.TenantId,
                request.Id,
                transaction,
                transactionCancellationToken);
            return deleted is ISuccess
                ? new DeleteDomainZoneResponse(request.Id)
                : (Failure)deleted;
        }, cancellationToken);
    }
}
