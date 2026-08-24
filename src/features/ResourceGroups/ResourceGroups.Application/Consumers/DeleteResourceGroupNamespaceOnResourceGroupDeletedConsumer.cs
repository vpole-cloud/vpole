using k8s;
using MassTransit;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Consumers;

/// <summary>
/// Supports resource groups created before RuntimeCell becomes their sole namespace owner.
/// </summary>
public sealed class DeleteKubernetesNamespaceOnResourceGroupDeletedConsumer(
    IKubernetesClientProvider kubernetesClientProvider)
    : IConsumer<ResourceGroupDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceGroupDeletedEvent> context)
    {
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(
            context.CancellationToken);
        await kubernetes.CoreV1.DeleteNamespaceAsync(
            context.Message.KubernetesNamespace,
            cancellationToken: context.CancellationToken);
    }
}
