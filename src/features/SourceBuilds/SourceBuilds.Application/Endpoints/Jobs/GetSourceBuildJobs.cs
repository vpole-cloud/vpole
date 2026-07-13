using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record GetSourceBuildJobsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] string? ConfigurationId = null,
    [FromQuery] string? WebServiceId = null,
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 20)
    : IMediatorRequest<PaginatedResponse<SourceBuildJobDto>>, ITenantReadOperation;

public sealed class GetSourceBuildJobsHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<GetSourceBuildJobsRequest, PaginatedResponse<SourceBuildJobDto>>
{
    public async Task<Result<PaginatedResponse<SourceBuildJobDto>>> HandleAsync(
        GetSourceBuildJobsRequest request,
        CancellationToken cancellationToken = default)
    {
        var filters = new List<FilterDefinition<SourceBuildJob>>
        {
            Builders<SourceBuildJob>.Filter.Eq(item => item.TenantId, request.TenantId)
        };
        if (!string.IsNullOrWhiteSpace(request.ConfigurationId))
            filters.Add(Builders<SourceBuildJob>.Filter.Eq(item => item.ConfigurationId, request.ConfigurationId));
        if (!string.IsNullOrWhiteSpace(request.WebServiceId))
            filters.Add(Builders<SourceBuildJob>.Filter.Eq(item => item.WebServiceId, request.WebServiceId));

        var filter = Builders<SourceBuildJob>.Filter.And(filters);
        var totalCount = await dbContext.SourceBuildJobs.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var jobs = await dbContext.SourceBuildJobs
            .Find(filter)
            .SortByDescending(item => item.CreatedAtUtc)
            .ApplyPagination(request.PageNumber, request.PageSize)
            .ToListAsync(cancellationToken);
        return new PaginatedResponse<SourceBuildJobDto>(
            jobs.Select(item => item.ToDto()).ToArray(),
            totalCount,
            request.PageNumber,
            request.PageSize);
    }
}

public sealed class GetSourceBuildJobsRequestValidator : AbstractValidator<GetSourceBuildJobsRequest>
{
    public GetSourceBuildJobsRequestValidator()
    {
        RuleFor(item => item.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}
