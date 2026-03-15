using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.Features.Database;
using Pole.Features.DomainNames.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DomainNames.Endpoints;

public static class GetById
{
    public const string Route = "/{Id}";

    public static IEndpointRouteBuilder MapGetDomainNameByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (IMediator mediator, [AsParameters] GetDomainNameByIdRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetDomainNameById")
            .WithDescription("Gets a domain name by its ID.")
            .Produces<DnsRecordDto>();

        return app;
    }
}

public sealed class GetDomainNameByIdHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetDomainNameByIdRequest, DnsRecordDto>
{
    public async Task<Result<DnsRecordDto>> HandleAsync(
        GetDomainNameByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var dn = await dbContext.DnsRecords
            .Find(d => d.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (dn is null)
            return Result.NotFound($"Domain name with ID '{request.Id}' not found.");

        return dn.ToDto();
    }
}

public record GetDomainNameByIdRequest(
    [FromRoute] string Id
) : IMediatorRequest<DnsRecordDto>;
