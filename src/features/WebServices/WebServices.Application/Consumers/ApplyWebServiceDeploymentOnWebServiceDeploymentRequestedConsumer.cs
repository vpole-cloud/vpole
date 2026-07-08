using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Consumers;

public sealed class ApplyWebServiceDeploymentOnWebServiceDeploymentRequestedConsumer(
    IWebServiceDbContext dbContext,
    IWebServiceDeploymentApplier deploymentApplier)
    : IConsumer<WebServiceDeploymentRequestedEvent>
{
    public Task Consume(ConsumeContext<WebServiceDeploymentRequestedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    public async Task Handle(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default)
    {
        var applyResult = await deploymentApplier.ApplyAsync(deploymentEvent, cancellationToken);

        if (applyResult is Failure failure)
        {
            await MarkReleaseAsync(
                deploymentEvent,
                WebServiceReleaseOutcome.Failed,
                failure.Message,
                cancellationToken);
            throw new InvalidOperationException(failure.Message);
        }

        await MarkReleaseAsync(
            deploymentEvent,
            WebServiceReleaseOutcome.Succeeded,
            failureMessage: null,
            cancellationToken);
    }

    private Task MarkReleaseAsync(
        WebServiceDeploymentRequestedEvent deploymentEvent,
        WebServiceReleaseOutcome outcome,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        var filter = Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(deploymentEvent.EventCore.TenantId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.WebServiceId, deploymentEvent.WebServiceId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.Id, deploymentEvent.ReleaseId));

        var update = Builders<WebServiceRelease>.Update
            .Set(release => release.Outcome, outcome)
            .Set(release => release.CompletedAtUtc, DateTime.UtcNow)
            .Set(release => release.FailureMessage, failureMessage);

        return dbContext.WebServiceReleases.UpdateOneAsync(
            filter,
            update,
            cancellationToken: cancellationToken);
    }
}
