using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
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
