using MassTransit;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.ResourceDeployments.Events;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Consumers;

public sealed class FinalizeFunctionAppDeletionConsumer(
    FinalizeFunctionAppDeletion finalizeFunctionAppDeletion)
    : IConsumer<ResourceDeploymentDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                FunctionResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return;
        }

        var result = await finalizeFunctionAppDeletion(
            message.EventCore.TenantId,
            message.ResourceId,
            message.ResourceDeploymentId,
            context.CancellationToken);
        if (!result.TryGetValue(out _, out var conflict))
        {
            throw new InvalidOperationException(
                $"Function App deletion finalization conflicted for '{conflict.FunctionAppId}'.");
        }
    }
}
