using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.Common.Abstractions;
using Pole.Server.Features.DomainNames.Common;
using Pole.Server.Mediator;
using Pole.Server.ResultType;
using System.Text.RegularExpressions;

namespace Pole.Server.Features.DomainNames.Endpoints;

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
            filter = Builders<DnsRecord>.Filter.Regex(d => d.Name, new BsonRegularExpression(pattern, "i"));
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

public record GetDomainNamesRequest(
    [FromQuery] string? Search = null)
    : PaginatedRequest<DnsRecordDto>
{
}
