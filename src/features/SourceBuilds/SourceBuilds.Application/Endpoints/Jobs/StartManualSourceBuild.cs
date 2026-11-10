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
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.GitProviders.Contracts.HttpMapping.GitProviderContractHttpMappings;
using static Pole.Features.SourceBuilds.Application.Common.SourceBuildHttpMappings;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

public sealed record StartManualSourceBuildRequest(
    [FromRoute] string ConfigurationId,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromClaim(Claims.Email)] ClaimValue<string?> CurrentUserEmail = default
) : IMediatorRequest<StartManualSourceBuildOutcome>, ITenantWriteOperation;

public union StartManualSourceBuildOutcome(
    SourceBuildJobDto,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationWebServiceNotFound,
    SourceBuildConfigurationUpdateConflict,
    SourceBuildConfigurationRepositoryChanged,
    SourceRepositoryBuildNotFound,
    SourceRepositoryBuildConnectionNotFound,
    SourceRepositoryRemoteNotFound,
    SourceRepositoryBranchNotFound,
    SourceRepositoryAccessDenied,
    SourceRepositoryUnavailable);

public sealed class StartManualSourceBuildHandler(
    LoadSourceBuildConfiguration loadSourceBuildConfiguration,
    ResolveSourceRepositoryCommit resolveCommit,
    QueueSourceBuildJob queueSourceBuildJob,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<StartManualSourceBuildRequest, StartManualSourceBuildOutcome>
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
            return new SourceBuildConfigurationNotFound(request.ConfigurationId);

        var resolved = await resolveCommit(
            new SourceRepositoryReferenceRequest(request.TenantId, configuration.SourceRepositoryId),
            cancellationToken);
        return resolved switch
        {
            SourceRepositoryCommitResolved commit => await QueueAsync(
                request,
                configuration,
                commit,
                cancellationToken),
            SourceRepositoryBuildNotFound error => error,
            SourceRepositoryBuildConnectionNotFound error => error,
            SourceRepositoryRemoteNotFound error => error,
            SourceRepositoryBranchNotFound error => error,
            SourceRepositoryAccessDenied error => error,
            SourceRepositoryUnavailable error => error,
        };
    }

    private async Task<StartManualSourceBuildOutcome> QueueAsync(
        StartManualSourceBuildRequest request,
        SourceBuildConfiguration configuration,
        SourceRepositoryCommitResolved commit,
        CancellationToken cancellationToken)
    {
        var build = SourceBuildJobFactory.CreateManual(
            request.TenantId,
            configuration.Id,
            commit.Repository.Id,
            configuration.WebServiceId,
            commit.Repository.Branch,
            commit.CommitSha,
            new SourceBuildInputVersion(
                configuration.Revision,
                commit.Repository.Revision,
                checked(configuration.BuildGeneration + 1)),
            configuration.Recipe,
            request.CurrentUserId.Value,
            request.CurrentUserEmail.Value,
            new SourceBuildJobFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        var queued = await queueSourceBuildJob(
            build,
            new SourceBuildRequestedEvent(build.Id) { EventCore = EventCore.Create(request.TenantId) },
            cancellationToken);
        return queued switch
        {
            SourceBuildJob success => success.ToDto(),
            SourceBuildWebServiceNotFound error => new SourceBuildConfigurationWebServiceNotFound(error.WebServiceId),
            SourceBuildConfigurationUpdateConflict error => error,
            SourceBuildConfigurationRepositoryChanged error => error,
        };
    }
}

public sealed class StartManualSourceBuildRequestValidator : AbstractValidator<StartManualSourceBuildRequest>
{
    public StartManualSourceBuildRequestValidator() =>
        RuleFor(item => item.ConfigurationId).MongoObjectId("Source build configuration ID");
}

public static class StartManualSourceBuildEndpoint
{
    public static IEndpointRouteBuilder MapManualSourceBuildEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{ConfigurationId}/builds",
                async (
                    IMediator mediator, [AsParameters] StartManualSourceBuildRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        SourceBuildJobDto job => Results.Accepted(value: job),
                        SourceBuildConfigurationNotFound error => MapConfigurationNotFound(error),
                        SourceBuildConfigurationWebServiceNotFound error => MapWebServiceNotFound(error),
                        SourceBuildConfigurationUpdateConflict error => MapConfigurationUpdateConflict(error),
                        SourceBuildConfigurationRepositoryChanged error => MapConfigurationRepositoryChanged(error),
                        SourceRepositoryBuildNotFound error => MapSourceRepositoryBuildNotFound(error),
                        SourceRepositoryBuildConnectionNotFound error => MapSourceRepositoryBuildConnectionNotFound(
                            error),
                        SourceRepositoryRemoteNotFound error => MapSourceRepositoryRemoteNotFound(error),
                        SourceRepositoryBranchNotFound error => MapSourceRepositoryBranchNotFound(error),
                        SourceRepositoryAccessDenied error => MapSourceRepositoryAccessDenied(error),
                        SourceRepositoryUnavailable error => MapSourceRepositoryUnavailable(error),
                    });
                })
            .WithName("StartManualSourceBuild")
            .Produces<SourceBuildJobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
