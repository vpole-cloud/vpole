using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionApps
{
    public static IEndpointRouteBuilder MapGetFunctionAppsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetFunctionAppsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetFunctionApps")
            .Produces<PaginatedResponse<FunctionAppDto>>();
        return app;
    }
}

public sealed record GetFunctionAppsRequest : PaginatedRequest<FunctionAppDto>, ITenantReadOperation
{
    [FromHeader(Name = TenantHeaders.TenantId)] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? ResourceGroupId { get; init; }
}

public sealed class GetFunctionAppsHandler(IFunctionAppDbContext dbContext)
    : IMediatorHandler<GetFunctionAppsRequest, PaginatedResponse<FunctionAppDto>>
{
    public async Task<Result<PaginatedResponse<FunctionAppDto>>> HandleAsync(
        GetFunctionAppsRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<FunctionApp>.Filter.ForTenant(request.TenantId);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var namePattern = new BsonRegularExpression($".*{Regex.Escape(request.Search)}.*", "i");
            var nameFilter = Builders<FunctionApp>.Filter.Regex(app => app.Name, namePattern);
            filter &= ObjectId.TryParse(request.Search, out _)
                ? Builders<FunctionApp>.Filter.Or(
                    nameFilter,
                    Builders<FunctionApp>.Filter.Eq(app => app.Id, request.Search))
                : nameFilter;
        }
        if (!string.IsNullOrWhiteSpace(request.ResourceGroupId))
            filter &= Builders<FunctionApp>.Filter.Eq(app => app.ResourceGroupId, request.ResourceGroupId);

        var totalCount = await dbContext.FunctionApps.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await dbContext.FunctionApps.Find(filter)
            .SortByDescending(app => app.CreatedAtUtc)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken);
        return new PaginatedResponse<FunctionAppDto>(
            items.Select(app => app.ToDto()),
            totalCount,
            request.PageNumber,
            request.PageSize);
    }
}
