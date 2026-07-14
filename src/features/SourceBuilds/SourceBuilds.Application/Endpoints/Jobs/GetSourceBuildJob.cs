using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record GetSourceBuildJobRequest(
    [FromRoute] string BuildJobId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceBuildJobDto>, ITenantReadOperation;

public sealed class GetSourceBuildJobHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<GetSourceBuildJobRequest, SourceBuildJobDto>
{
    public async Task<Result<SourceBuildJobDto>> HandleAsync(
        GetSourceBuildJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var job = await dbContext.SourceBuildJobs
            .Find(item => item.TenantId == request.TenantId && item.Id == request.BuildJobId)
            .FirstOrDefaultAsync(cancellationToken);
        return job is null
            ? Failures.NotFound("Source build job not found.", request.BuildJobId)
            : job.ToDto();
    }
}

public static class GetSourceBuildJobEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildJobEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{BuildJobId}", async (IMediator mediator, [AsParameters] GetSourceBuildJobRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildJob")
            .Produces<SourceBuildJobDto>();
        return app;
    }
}
