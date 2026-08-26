using MassTransit;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.WebServices.Events;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class CompleteSourceBuildOnDeploymentConsumer(
    CompleteSourceBuildJobOnDeployment completeSourceBuildJobOnDeployment,
    TimeProvider timeProvider)
    : IConsumer<WebServiceDeploymentCompletedEvent>
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
