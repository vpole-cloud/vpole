using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Consumers;

public sealed class WebServiceDeploymentConsumer(
    IWebServiceDbContext dbContext,
    TimeProvider timeProvider)
    : IConsumer<ResourceDeploymentAppliedEvent>,
      IConsumer<ResourceDeploymentFailedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentFailedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public Task Handle(
        ResourceDeploymentAppliedEvent deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                deploymentEvent.ResourceType,
                ResourceDeploymentResourceTypes.WebService.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var pendingReleaseFilter = Builders<WebServiceRelease>.Filter.And(
                Builders<WebServiceRelease>.Filter.ForTenant(
                    deploymentEvent.EventCore.TenantId),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.WebServiceId,
                    deploymentEvent.ResourceId),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.DeploymentGeneration,
                    deploymentEvent.Generation),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.Outcome,
                    WebServiceReleaseOutcome.Pending));

            var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var release = await dbContext.WebServiceReleases.FindOneAndUpdateAsync(
                session,
                pendingReleaseFilter,
                Builders<WebServiceRelease>.Update
                    .Set(item => item.Outcome, WebServiceReleaseOutcome.Succeeded)
                    .Set(item => item.CompletedAtUtc, completedAtUtc)
                    .Set(item => item.FailureMessage, null),
                new FindOneAndUpdateOptions<WebServiceRelease>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            if (release is null)
                return Pole.ResultType.Result.Success();

            await dbContext.StoreEventAsync(
                new WebServiceDeploymentCompletedEvent(
                    release.Id,
                    release.WebServiceId,
                    Succeeded: true,
                    FailureMessage: null)
                {
                    EventCore = deploymentEvent.EventCore
                },
                session,
                ct);
            return Pole.ResultType.Result.Success();
        }, cancellationToken: cancellationToken);
    }

    public Task Handle(
        ResourceDeploymentFailedEvent deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        if (deploymentEvent.Retryable ||
            !string.Equals(
                deploymentEvent.ResourceType,
                ResourceDeploymentResourceTypes.WebService.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var pendingReleaseFilter = Builders<WebServiceRelease>.Filter.And(
                Builders<WebServiceRelease>.Filter.ForTenant(
                    deploymentEvent.EventCore.TenantId),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.WebServiceId,
                    deploymentEvent.ResourceId),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.DeploymentGeneration,
                    deploymentEvent.Generation),
                Builders<WebServiceRelease>.Filter.Eq(
                    release => release.Outcome,
                    WebServiceReleaseOutcome.Pending));

            var failureMessage =
                $"Deployment failed ({deploymentEvent.FailureCode}).";
            var release = await dbContext.WebServiceReleases.FindOneAndUpdateAsync(
                session,
                pendingReleaseFilter,
                Builders<WebServiceRelease>.Update
                    .Set(item => item.Outcome, WebServiceReleaseOutcome.Failed)
                    .Set(
                        item => item.CompletedAtUtc,
                        timeProvider.GetUtcNow().UtcDateTime)
                    .Set(item => item.FailureMessage, failureMessage),
                new FindOneAndUpdateOptions<WebServiceRelease>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            if (release is null)
                return Pole.ResultType.Result.Success();

            await dbContext.StoreEventAsync(
                new WebServiceDeploymentCompletedEvent(
                    release.Id,
                    release.WebServiceId,
                    Succeeded: false,
                    failureMessage)
                {
                    EventCore = deploymentEvent.EventCore
                },
                session,
                ct);
            return Pole.ResultType.Result.Success();
        }, cancellationToken: cancellationToken);
    }
}
