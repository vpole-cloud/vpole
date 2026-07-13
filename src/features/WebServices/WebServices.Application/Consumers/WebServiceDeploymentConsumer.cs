using MassTransit;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Consumers;

public sealed class WebServiceDeploymentConsumer(
    IWebServiceDbContext dbContext,
    IWebServiceDeploymentApplier deploymentApplier,
    ILogger<WebServiceDeploymentConsumer> logger)
    : IConsumer<WebServiceDeploymentRequestedEvent>
{
    public Task Consume(ConsumeContext<WebServiceDeploymentRequestedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public async Task Handle(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default)
    {
        var applyResult = await deploymentApplier.ApplyAsync(deploymentEvent, cancellationToken);

        if (applyResult is Failure failure)
        {
            if (failure is ExternalServiceFailure { ServiceName: "Kubernetes" })
            {
                logger.LogError(
                    "[web-service-deployment-failed] Kubernetes could not apply release {ReleaseId} for web service {WebServiceId}: {Reason}",
                    deploymentEvent.ReleaseId,
                    deploymentEvent.WebServiceId,
                    failure.Message);
            }

            var completion = await MarkReleaseAsync(
                deploymentEvent,
                WebServiceReleaseOutcome.Failed,
                failure.Message,
                cancellationToken);
            if (completion is Failure completionFailure)
                throw new InvalidOperationException(completionFailure.Message);
            throw new InvalidOperationException(failure.Message);
        }

        var succeeded = await MarkReleaseAsync(
            deploymentEvent,
            WebServiceReleaseOutcome.Succeeded,
            failureMessage: null,
            cancellationToken);
        if (succeeded is Failure succeededFailure)
            throw new InvalidOperationException(succeededFailure.Message);
    }

    private Task<Result> MarkReleaseAsync(
        WebServiceDeploymentRequestedEvent deploymentEvent,
        WebServiceReleaseOutcome outcome,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        var filter = Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(deploymentEvent.EventCore.TenantId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.WebServiceId, deploymentEvent.WebServiceId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.Id, deploymentEvent.ReleaseId));

        return dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var update = Builders<WebServiceRelease>.Update
                .Set(release => release.Outcome, outcome)
                .Set(release => release.CompletedAtUtc, DateTime.UtcNow)
                .Set(release => release.FailureMessage, failureMessage);
            var updated = await dbContext.WebServiceReleases.UpdateOneAsync(
                session,
                Builders<WebServiceRelease>.Filter.And(
                    filter,
                    Builders<WebServiceRelease>.Filter.Eq(
                        release => release.Outcome,
                        WebServiceReleaseOutcome.Pending)),
                update,
                cancellationToken: ct);
            if (updated.MatchedCount != 1)
            {
                var existing = await dbContext.WebServiceReleases
                    .Find(session, filter)
                    .FirstOrDefaultAsync(ct);
                if (existing is null)
                    return Failures.NotFound("Web service release not found.", deploymentEvent.ReleaseId);
                return existing.Outcome == outcome
                    ? Result.Success()
                    : Failures.Conflict("The web service release already has a different outcome.");
            }

            await dbContext.StoreEventAsync(
                new WebServiceDeploymentCompletedEvent(
                    deploymentEvent.ReleaseId,
                    deploymentEvent.WebServiceId,
                    outcome == WebServiceReleaseOutcome.Succeeded,
                    failureMessage)
                {
                    EventCore = deploymentEvent.EventCore
                },
                session,
                ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }
}
