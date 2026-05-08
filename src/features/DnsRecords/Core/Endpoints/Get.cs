using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Domain.Models;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Feature.DnsRecords.Core.Endpoints;

public static class Get
{
    public static IEndpointRouteBuilder MapGetDomainNamesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetDomainNamesRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetDomainNames")
            .WithSummary("Retrieves all domain names.")
            .WithDescription("Fetches a list of all domain names available in the system.")
            .Produces<List<DnsRecordDto>>();

        return app;
    }
}

public sealed class GetDomainNamesHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetDomainNamesRequest, PaginatedResponse<DnsRecordDto>>
{
    public async Task<Result<PaginatedResponse<DnsRecordDto>>> HandleAsync(
        GetDomainNamesRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DnsRecord>.Filter.Empty;

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $".*{Regex.Escape(request.Search)}.*";
            filter = Builders<DnsRecord>.Filter.Regex(d => d.Domain, new BsonRegularExpression(pattern, "i"));
        }

        var totalCount = await dbContext.DnsRecords
            .CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var items = await dbContext.DnsRecords
            .Find(filter)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new PaginatedResponse<DnsRecordDto>(
            Items: items.Select(x => x.ToDto()),
            TotalCount: totalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetDomainNamesRequest : PaginatedRequest<DnsRecordDto>
{
    [FromQuery] public string? Search { get; init; }
}
