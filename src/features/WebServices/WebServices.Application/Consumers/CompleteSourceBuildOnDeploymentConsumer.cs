using MassTransit;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Consumers;

/// <summary>
///     Adapts the WebServices-owned deployment-completed event to the
///     SourceBuilds-owned completion contribution. Keeping the event contract and
///     its payload unchanged preserves pending outbox messages and consumers.
/// </summary>
public sealed class CompleteSourceBuildOnDeploymentConsumer(
    CompleteSourceBuildJobOnDeployment completeSourceBuildJobOnDeployment,
    TimeProvider timeProvider
) : IConsumer<WebServiceDeploymentCompletedEvent>
{
    public Task Consume(ConsumeContext<WebServiceDeploymentCompletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        WebServiceDeploymentCompletedEvent deployment,
        CancellationToken cancellationToken = default)
    {
        if (!deployment.ReleaseTerminal)
            return;

        await completeSourceBuildJobOnDeployment(
            new SourceBuildDeploymentCompletion(
                deployment.EventCore.TenantId,
                deployment.ReleaseId,
                deployment.ReleaseOutcome,
                deployment.FailureMessage,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }
}
