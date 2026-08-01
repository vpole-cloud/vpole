using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionRevisions
{
    public static IEndpointRouteBuilder MapGetFunctionRevisionsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/revisions", async (
                IMediator mediator,
                [AsParameters] GetFunctionRevisionsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetFunctionRevisions")
            .Produces<IReadOnlyList<FunctionRevisionDto>>();
        return app;
    }
}

public sealed record GetFunctionRevisionsRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<FunctionRevisionDto>>, ITenantReadOperation;

public sealed class GetFunctionRevisionsHandler(IFunctionAppDbContext dbContext)
    : IMediatorHandler<GetFunctionRevisionsRequest, IReadOnlyList<FunctionRevisionDto>>
{
    public async Task<Result<IReadOnlyList<FunctionRevisionDto>>> HandleAsync(
        GetFunctionRevisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var appFilter = Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.ForTenant(request.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, request.Id));
        var functionApp = await dbContext.FunctionApps.Find(appFilter).FirstOrDefaultAsync(cancellationToken);
        if (functionApp is null)
            return Failures.NotFound("Function App not found.", request.Id);

        var revisions = await dbContext.FunctionRevisions
            .Find(Builders<FunctionRevision>.Filter.And(
                Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, functionApp.TenantId),
                Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, functionApp.Id)))
            .SortByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);
        return revisions.Select(revision => revision.ToDto()).ToArray();
    }
}
