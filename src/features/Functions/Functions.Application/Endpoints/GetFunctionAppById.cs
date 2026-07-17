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

public static class GetFunctionAppById
{
    public static IEndpointRouteBuilder MapGetFunctionAppByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetFunctionAppByIdRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetFunctionAppById")
            .Produces<FunctionAppDetailsDto>();
        return app;
    }
}

public sealed record GetFunctionAppByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId)
    : IMediatorRequest<FunctionAppDetailsDto>, ITenantReadOperation;

public sealed class GetFunctionAppByIdHandler(IFunctionAppDbContext dbContext)
    : IMediatorHandler<GetFunctionAppByIdRequest, FunctionAppDetailsDto>
{
    public async Task<Result<FunctionAppDetailsDto>> HandleAsync(
        GetFunctionAppByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var appFilter = Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.ForTenant(request.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, request.Id));
        var functionApp = await dbContext.FunctionApps
            .Find(appFilter)
            .FirstOrDefaultAsync(cancellationToken);
        if (functionApp is null)
            return Failures.NotFound("Function App not found.", request.Id);

        var revisionIds = new[] { functionApp.ActiveRevisionId, functionApp.PendingRevisionId }
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        var revisions = revisionIds.Length == 0
            ? []
            : await dbContext.FunctionRevisions
                .Find(Builders<FunctionRevision>.Filter.And(
                    Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, functionApp.TenantId),
                    Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, functionApp.Id),
                    Builders<FunctionRevision>.Filter.In(revision => revision.Id, revisionIds)))
                .ToListAsync(cancellationToken);

        var active = revisions.FirstOrDefault(revision => revision.Id == functionApp.ActiveRevisionId);
        var pending = revisions.FirstOrDefault(revision => revision.Id == functionApp.PendingRevisionId);
        return new FunctionAppDetailsDto(
            functionApp.ToDto(),
            active?.ToDto(),
            pending?.ToDto());
    }
}
