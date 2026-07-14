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
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record StartManualSourceBuildRequest(
    [FromRoute] string ConfigurationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromClaim(Claims.Email)] ClaimValue<string?> CurrentUserEmail = default)
    : IMediatorRequest<SourceBuildJobDto>, ITenantWriteOperation;

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

public sealed class StartManualSourceBuildRequestValidator : AbstractValidator<StartManualSourceBuildRequest>
{
    public StartManualSourceBuildRequestValidator() =>
        RuleFor(item => item.ConfigurationId).NotEmpty();
}

public static class StartManualSourceBuildEndpoint
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
}
