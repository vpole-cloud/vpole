using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Feature.DnsRecords.Core.Common;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Feature.DnsRecords.Core.Endpoints;

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
    IDnsRecordDbContext dbContext)
    : IMediatorHandler<GetDomainNameByIdRequest, DnsRecordDto>
{
    public async Task<Result<DnsRecordDto>> HandleAsync(
        GetDomainNameByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DnsRecord>.Filter.And(
            Builders<DnsRecord>.Filter.ForTenant(request.TenantId),
            Builders<DnsRecord>.Filter.OfType<DnsRecordStatus, Committed>(record => record.Status),
            Builders<DnsRecord>.Filter.Eq(d => d.Id, request.Id));

        var dn = await dbContext.DnsRecords
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (dn is null)
            return Result.NotFound($"Domain name with ID '{request.Id}' not found.");

        return dn.ToDto();
    }
}

public record GetDomainNameByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<DnsRecordDto>, ITenantReadOperation;
