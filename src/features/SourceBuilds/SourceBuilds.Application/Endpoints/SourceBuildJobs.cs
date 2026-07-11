using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public static class SourceBuildJobs
{
    public static IEndpointRouteBuilder MapManualSourceBuildEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConfigurationId}/builds", async (IMediator mediator,
                [AsParameters] StartManualSourceBuildRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(value => Results.Accepted(value: value));
            })
            .WithName("StartManualSourceBuild")
            .Produces<SourceBuildJobDto>(StatusCodes.Status202Accepted);
        return app;
    }

    public static IEndpointRouteBuilder MapSourceBuildJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceBuildJobsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildJobs")
            .Produces<SourceBuildJobPageDto>();

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

public sealed record StartManualSourceBuildRequest(
    [FromRoute] string ConfigurationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromClaim(Claims.Email)] ClaimValue<string?> CurrentUserEmail = default)
    : IMediatorRequest<SourceBuildJobDto>, ITenantWriteOperation;

public sealed record GetSourceBuildJobsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] string? ConfigurationId = null,
    [FromQuery] string? WebServiceId = null,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 20)
    : IMediatorRequest<SourceBuildJobPageDto>, ITenantReadOperation;

public sealed record GetSourceBuildJobRequest(
    [FromRoute] string BuildJobId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceBuildJobDto>, ITenantReadOperation;

public sealed class StartManualSourceBuildHandler(
    ISourceBuildDbContext dbContext,
    IGitProviderDbContext gitProviderDbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<StartManualSourceBuildRequest, SourceBuildJobDto>
{
    public async Task<Result<SourceBuildJobDto>> HandleAsync(
        StartManualSourceBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = await dbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == request.TenantId && item.Id == request.ConfigurationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (configuration is null)
            return Failures.NotFound("Source build configuration not found.", request.ConfigurationId);

        var sourceRepository = await gitProviderDbContext.SourceRepositories
            .Find(item =>
                item.TenantId == request.TenantId &&
                item.Id == configuration.SourceRepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceRepository is null)
            return Failures.Conflict("The linked source repository no longer exists.");
        if (!string.Equals(
                sourceRepository.CandidateKey,
                configuration.Recipe.CandidateKey,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The linked source candidate changed; update or recreate the build configuration.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var build = SourceBuildJobFactory.CreateManual(
            request.TenantId,
            configuration.Id,
            sourceRepository.Id,
            configuration.WebServiceId,
            sourceRepository.Branch,
            sourceRepository.CommitSha,
            configuration.Recipe,
            request.CurrentUserId.Value,
            request.CurrentUserEmail.Value,
            new SourceBuildJobFactory.CreateOptions(
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                now));

        return await dbContext.RunInTransactionAsync<SourceBuildJobDto>(async (session, ct) =>
        {
            await dbContext.SourceBuildJobs.InsertOneAsync(session, build, cancellationToken: ct);
            await dbContext.StoreEventAsync(
                new SourceBuildRequestedEvent(build.Id)
                {
                    EventCore = EventCore.Create(request.TenantId)
                },
                session,
                ct);
            return build.ToDto();
        }, cancellationToken: cancellationToken);
    }
}

public sealed class GetSourceBuildJobsHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<GetSourceBuildJobsRequest, SourceBuildJobPageDto>
{
    public async Task<Result<SourceBuildJobPageDto>> HandleAsync(
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
            .Skip((request.Page - 1) * request.PageSize)
            .Limit(request.PageSize)
            .ToListAsync(cancellationToken);
        return new SourceBuildJobPageDto(
            jobs.Select(item => item.ToDto()).ToArray(),
            request.Page,
            request.PageSize,
            totalCount);
    }
}

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

public sealed class StartManualSourceBuildRequestValidator : AbstractValidator<StartManualSourceBuildRequest>
{
    public StartManualSourceBuildRequestValidator() =>
        RuleFor(item => item.ConfigurationId).NotEmpty();
}

public sealed class GetSourceBuildJobsRequestValidator : AbstractValidator<GetSourceBuildJobsRequest>
{
    public GetSourceBuildJobsRequestValidator()
    {
        RuleFor(item => item.Page).GreaterThanOrEqualTo(1);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}
