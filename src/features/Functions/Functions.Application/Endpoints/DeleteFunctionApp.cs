using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class DeleteFunctionApp
{
    public static IEndpointRouteBuilder MapDeleteFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Accepted(value: response));
            })
            .WithName("DeleteFunctionApp")
            .Produces<DeleteFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}

public sealed record DeleteFunctionAppRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteFunctionAppResponse>, ITenantWriteOperation;

public sealed record DeleteFunctionAppResponse(string FunctionAppId);

public sealed class DeleteFunctionAppHandler(IFunctionAppDbContext dbContext, TimeProvider timeProvider)
    : IMediatorHandler<DeleteFunctionAppRequest, DeleteFunctionAppResponse>
{
    public async Task<Result<DeleteFunctionAppResponse>> HandleAsync(
        DeleteFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var identityFilter = Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.ForTenant(request.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, request.Id));
        var app = await dbContext.FunctionApps.Find(identityFilter).FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", request.Id);
        if (app.DesiredState == FunctionAppDesiredState.Deleting)
            return new DeleteFunctionAppResponse(app.Id);

        return await dbContext.RunInTransactionAsync<DeleteFunctionAppResponse>(async (
            session,
            transactionCancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var appResult = await dbContext.FunctionApps.UpdateOneAsync(
                session,
                Builders<FunctionApp>.Filter.And(
                    identityFilter,
                    Builders<FunctionApp>.Filter.Ne(
                        current => current.DesiredState,
                        FunctionAppDesiredState.Deleting)),
                Builders<FunctionApp>.Update
                    .Set(current => current.DesiredState, FunctionAppDesiredState.Deleting)
                    .Set(current => current.Status, FunctionAppStatus.Deleting)
                    .Set(current => current.UpdatedAtUtc, now),
                cancellationToken: transactionCancellationToken);
            if (appResult.MatchedCount != 1)
                return Failures.Conflict("Function App state changed concurrently.");

            await dbContext.FunctionRouteBindings.UpdateOneAsync(
                session,
                route => route.NormalizedRoute == app.FunctionRoute &&
                         route.TenantId == app.TenantId &&
                         route.FunctionAppId == app.Id,
                Builders<FunctionRouteBinding>.Update
                    .Set(route => route.State, FunctionRouteBindingState.Disabled)
                    .Set(route => route.UpdatedAtUtc, now),
                cancellationToken: transactionCancellationToken);
            await dbContext.FunctionTimerStates.UpdateManyAsync(
                session,
                timer => timer.TenantId == app.TenantId &&
                         timer.FunctionAppId == app.Id &&
                         timer.Enabled,
                Builders<FunctionTimerState>.Update
                    .Set(timer => timer.Enabled, false)
                    .Set(timer => timer.Lease, null)
                    .Set(timer => timer.UpdatedAtUtc, now),
                cancellationToken: transactionCancellationToken);
            await dbContext.StoreEventAsync(
                new FunctionAppDeleteRequestedEvent(app.Id, app.ActiveRevisionId)
                {
                    EventCore = EventCore.Create(app.TenantId)
                },
                session,
                transactionCancellationToken);
            return new DeleteFunctionAppResponse(app.Id);
        }, cancellationToken: cancellationToken);
    }
}
