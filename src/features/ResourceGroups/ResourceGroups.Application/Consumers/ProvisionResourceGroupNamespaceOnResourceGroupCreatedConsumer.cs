using k8s;
using k8s.Models;
using MassTransit;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Consumers;

/// <summary>
/// Supports resource groups created before RuntimeCell becomes their sole namespace owner.
/// </summary>
public sealed class ProvisionResourceGroupOnResourceGroupCreatedConsumer(
    IKubernetesClientProvider kubernetesClientProvider)
    : IConsumer<ResourceGroupCreatedEvent>
{
    public async Task Consume(ConsumeContext<ResourceGroupCreatedEvent> context)
    {
        var message = context.Message;
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(
            context.CancellationToken);
        await kubernetes.CoreV1.CreateNamespaceAsync(new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = message.KubernetesNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["pole.io/tenant-id"] = message.EventCore.TenantId,
                    ["pole.io/resource-group-id"] = message.ResourceGroupId
                }
            }
        }, cancellationToken: context.CancellationToken);
    }
}
