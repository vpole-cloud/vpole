using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

public sealed record StartManualSourceBuildRequest(
    [FromRoute] string ConfigurationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromClaim(Claims.Email)] ClaimValue<string?> CurrentUserEmail = default)
    : IMediatorRequest<StartManualSourceBuildOutcome>, ITenantWriteOperation;

public union StartManualSourceBuildError(
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationWebServiceNotFound,
    SourceBuildLinkedRepositoryNotFound,
    SourceBuildLinkedRepositoryCandidateChanged);

public union StartManualSourceBuildOutcome(
    SourceBuildJobDto,
    StartManualSourceBuildError);

public sealed class StartManualSourceBuildHandler(
    LoadSourceBuildConfiguration loadSourceBuildConfiguration,
    FindSourceRepositoryBuild findSourceRepositoryBuild,
    QueueSourceBuildJob queueSourceBuildJob,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<StartManualSourceBuildRequest, StartManualSourceBuildOutcome>
{
    public async Task<StartManualSourceBuildOutcome> HandleAsync(
        StartManualSourceBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = await loadSourceBuildConfiguration(
            request.TenantId,
            request.ConfigurationId,
            cancellationToken);
        if (configuration is null)
        {
            return new StartManualSourceBuildError(
                new SourceBuildConfigurationNotFound(request.ConfigurationId));
        }

        var sourceRepository = await findSourceRepositoryBuild(
            request.TenantId,
            configuration.SourceRepositoryId,
            cancellationToken);
        return sourceRepository switch
        {
            Err<FindSourceRepositoryBuildError> => new StartManualSourceBuildError(
                new SourceBuildLinkedRepositoryNotFound(
                    configuration.Id,
                    configuration.SourceRepositoryId)),
            Ok<SourceRepositoryBuildSnapshot> success => await QueueAsync(
                request,
                configuration,
                success.Value,
                queueSourceBuildJob,
                timeProvider,
                cancellationToken)
        };
    }

    private static async Task<StartManualSourceBuildOutcome> QueueAsync(
        StartManualSourceBuildRequest request,
        SourceBuildConfiguration configuration,
        SourceRepositoryBuildSnapshot sourceRepository,
        QueueSourceBuildJob queueSourceBuildJob,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                sourceRepository.CandidateKey,
                configuration.Recipe.CandidateKey,
                StringComparison.Ordinal))
        {
            return new StartManualSourceBuildError(
                new SourceBuildLinkedRepositoryCandidateChanged(
                    configuration.Id,
                    sourceRepository.Id));
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
                ObjectId.GenerateNewId().ToString(),
                now));
        var queued = await queueSourceBuildJob(
            build,
            new SourceBuildRequestedEvent(build.Id)
            {
                EventCore = EventCore.Create(request.TenantId)
            },
            cancellationToken);
        return queued switch
        {
            Ok<SourceBuildJob> success => success.Value.ToDto(),
            Err<SourceBuildWebServiceNotFound> error =>
                new StartManualSourceBuildError(
                    new SourceBuildConfigurationWebServiceNotFound(error.Value.WebServiceId))
        };
    }
}

public sealed class StartManualSourceBuildRequestValidator
    : AbstractValidator<StartManualSourceBuildRequest>
{
    public StartManualSourceBuildRequestValidator() =>
        RuleFor(item => item.ConfigurationId).MongoObjectId("Source build configuration ID");
}

public static class StartManualSourceBuildEndpoint
{
    public static IEndpointRouteBuilder MapManualSourceBuildEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConfigurationId}/builds", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] StartManualSourceBuildRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildJobDto job => Results.Accepted(value: job),
                    StartManualSourceBuildError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("StartManualSourceBuild")
            .Produces<SourceBuildJobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
